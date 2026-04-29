using Azure.Communication.CallAutomation;
using Azure.Communication;
using Azure.Identity;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;
using CallAutomation.AzureAI.VoiceLive;

var builder = WebApplication.CreateBuilder(args);

// Application Insights — SDK auto-detects APPLICATIONINSIGHTS_CONNECTION_STRING env var (set by Bicep)
builder.Services.AddApplicationInsightsTelemetry();

// Get ACS Connection String from config
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

// Call Automation Client
var client = new CallAutomationClient(acsConnectionString);

// Credential singleton for Azure OpenAI Realtime API
var aiCredential = new DefaultAzureCredential();

// Pre-warm the token AND validate Voice Live WebSocket connectivity at startup.
// This catches RBAC/auth issues immediately instead of silently failing on the first call.
try
{
    var warmupStart = DateTime.UtcNow;
    var tokenResult = await aiCredential.GetTokenAsync(
        new Azure.Core.TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
        CancellationToken.None);
    var warmupMs = (DateTime.UtcNow - warmupStart).TotalMilliseconds;
    Console.WriteLine($"✓ AI credential pre-warmed in {warmupMs:F0}ms (expires {tokenResult.ExpiresOn:HH:mm:ss})");

    // Validate Voice Live WebSocket auth by doing a real connect+disconnect.
    // This catches missing RBAC roles (Cognitive Services User / OpenAI User) at startup
    // rather than discovering them on the first live phone call.
    var voiceLiveEndpoint = builder.Configuration.GetValue<string>("AzureOpenAI:Endpoint");
    var voiceLiveModel = builder.Configuration.GetValue<string>("AzureOpenAI:DeploymentName") ?? "gpt-realtime";
    if (!string.IsNullOrEmpty(voiceLiveEndpoint))
    {
        var wsUrl = new Uri($"{voiceLiveEndpoint.TrimEnd('/').Replace("https", "wss")}/voice-live/realtime?api-version=2025-10-01&x-ms-client-request-id={Guid.NewGuid()}&model={voiceLiveModel}");
        using var probeWs = new System.Net.WebSockets.ClientWebSocket();
        probeWs.Options.SetRequestHeader("Authorization", $"Bearer {tokenResult.Token}");
        using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await probeWs.ConnectAsync(wsUrl, probeCts.Token);
        await probeWs.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "startup probe", CancellationToken.None);
        Console.WriteLine($"✓ Voice Live WebSocket probe OK (model: {voiceLiveModel})");
    }
}
catch (System.Net.WebSockets.WebSocketException ex) when (ex.Message.Contains("401"))
{
    Console.WriteLine($"✗ FATAL: Voice Live WebSocket returned 401 — RBAC roles are missing on this managed identity!");
    Console.WriteLine($"  Ensure 'Cognitive Services OpenAI User' AND 'Cognitive Services User' roles are assigned.");
    Console.WriteLine($"  Calls WILL FAIL until this is fixed. Re-deploy infra or assign roles manually.");
    // Don't crash — let the app start so monitoring/health endpoints work, but log prominently
}
catch (Exception ex)
{
    Console.WriteLine($"⚠ AI credential/Voice Live pre-warm failed (will retry on first call): {ex.Message}");
}

// Per-call system prompts and languages (keyed by callback context ID)
var callPrompts = new ConcurrentDictionary<string, string>();
var callLanguages = new ConcurrentDictionary<string, string>();
var callLanguageCodes = new ConcurrentDictionary<string, string>();
var callTranscriptionHints = new ConcurrentDictionary<string, string>();
var callPhoneNumbers = new ConcurrentDictionary<string, string>();
// Track active call connection IDs (keyed by WebSocket contextId)
var callConnections = new ConcurrentDictionary<string, string>();
// Live transcription channels (keyed by contextId) — SSE consumers read from these
var transcriptionChannels = new ConcurrentDictionary<string, Channel<TranscriptionEvent>>();
// Live analysis channels (keyed by contextId) — SSE consumers read analysis results
var analysisChannels = new ConcurrentDictionary<string, Channel<AnalysisResult>>();
// Pre-warmed Voice Live WebSockets (keyed by contextId). For outbound calls we open
// the WS to Voice Live during the PSTN dial wait (~5-8s) so the hot path can skip
// token+connect (~300-600ms). Stored as (ws, createdAtUtc) so the cleanup loop can
// drop unclaimed warm sockets if the call never picked up. See /api/outboundCall.
var warmVoiceLiveSockets = new ConcurrentDictionary<string, (System.Net.WebSockets.ClientWebSocket Ws, DateTime CreatedAt)>();

// Global call log — subscriber channels for broadcasting call events to multiple SSE clients
var callLogSubscribers = new ConcurrentDictionary<string, Channel<CallLogEntry>>();
// Keep a history so late-connecting SSE clients see recent entries
var callLogHistory = new ConcurrentBag<CallLogEntry>();

var app = builder.Build();

// Get app base URL from multiple sources (priority order)
var appBaseUrl = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');

if (string.IsNullOrEmpty(appBaseUrl))
{
    appBaseUrl = builder.Configuration.GetValue<string>("AppBaseUrl");
}

if (string.IsNullOrEmpty(appBaseUrl))
{
    // Container Apps set CONTAINER_APP_HOSTNAME
    var containerAppHostname = Environment.GetEnvironmentVariable("CONTAINER_APP_HOSTNAME");
    if (!string.IsNullOrEmpty(containerAppHostname))
    {
        appBaseUrl = $"https://{containerAppHostname}";
    }
}

if (string.IsNullOrEmpty(appBaseUrl))
{
    var websiteHostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
    if (!string.IsNullOrEmpty(websiteHostname))
    {
        appBaseUrl = $"https://{websiteHostname}";
    }
}

if (string.IsNullOrEmpty(appBaseUrl))
{
    throw new InvalidOperationException("AppBaseUrl must be configured");
}

Console.WriteLine($"✓ App Base URL: {appBaseUrl}");

app.MapGet("/", () => "Caller Agent — ACS Call Automation API");

// ---------------------------------------------------------------------------
// POST /api/outboundCall — Initiate an outbound AI phone call
// ---------------------------------------------------------------------------
app.MapPost("/api/outboundCall", async (
    [FromBody] OutboundCallRequest request,
    ILogger<Program> logger,
    TelemetryClient telemetryClient) =>
{
    var contextId = Guid.NewGuid().ToString();
    var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{contextId}?callerId={request.PhoneNumber}");
    var websocketUri = appBaseUrl.Replace("https", "wss") + $"/ws?contextId={contextId}";

    var acsPhoneNumber = builder.Configuration.GetValue<string>("AcsPhoneNumber");
    ArgumentNullException.ThrowIfNullOrEmpty(acsPhoneNumber, "AcsPhoneNumber is not configured");

    // Store per-call system prompt and language if provided
    if (!string.IsNullOrEmpty(request.SystemPrompt))
    {
        callPrompts[contextId] = request.SystemPrompt;
    }
    else if (!string.IsNullOrEmpty(request.Purpose))
    {
        var nameClause = !string.IsNullOrEmpty(request.Name)
            ? $" You are calling {request.Name}."
            : "";
        callPrompts[contextId] = $"You are making an outbound phone call.{nameClause} Your goal: {request.Purpose}.";
    }

    // Store phone number for telemetry correlation
    callPhoneNumbers[contextId] = request.PhoneNumber;

    // Store language name and ISO 639-1 code (both classified by the AI)
    if (!string.IsNullOrEmpty(request.Language))
    {
        callLanguages[contextId] = request.Language;
    }
    if (!string.IsNullOrEmpty(request.LanguageCode))
    {
        callLanguageCodes[contextId] = request.LanguageCode;
    }
    if (!string.IsNullOrEmpty(request.TranscriptionHint))
    {
        callTranscriptionHints[contextId] = request.TranscriptionHint;
    }

    // Create a transcription channel for this call so the SSE endpoint can stream events
    var transcriptionChannel = Channel.CreateUnbounded<TranscriptionEvent>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true
    });
    transcriptionChannels[contextId] = transcriptionChannel;

    // Create an analysis channel for this call
    var analysisChannel = Channel.CreateUnbounded<AnalysisResult>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true
    });
    analysisChannels[contextId] = analysisChannel;

    // Force a fresh token before placing the call. Passing claims: "{}" bypasses
    // Azure.Core's token cache so we always get a brand-new token. ACS takes
    // several seconds to dial, so the token will still be valid when the
    // WebSocket connects and AzureVoiceLiveService uses it.
    var freshToken = await aiCredential.GetTokenAsync(
        new Azure.Core.TokenRequestContext(
            scopes: new[] { "https://cognitiveservices.azure.com/.default" },
            claims: "{}"),
        CancellationToken.None);

    // Fire-and-forget: pre-warm the Voice Live WebSocket in parallel with the PSTN
    // dial. By the time the callee picks up (~5-8s later) and ACS opens the media
    // WebSocket to /ws, this socket is already connected — saving ~300-600ms off
    // "callee picks up → first AI word". Falls back to cold connect if pre-warm
    // fails or is closed by the time the WS is claimed.
    _ = Task.Run(async () =>
    {
        try
        {
            var voiceLiveEndpoint = builder.Configuration.GetValue<string>("AzureOpenAI:Endpoint");
            var voiceLiveModel = builder.Configuration.GetValue<string>("AzureOpenAI:DeploymentName") ?? "gpt-realtime";
            if (string.IsNullOrEmpty(voiceLiveEndpoint)) return;

            var wsUrl = new Uri($"{voiceLiveEndpoint.TrimEnd('/').Replace("https", "wss")}/voice-live/realtime?api-version=2025-10-01&x-ms-client-request-id={Guid.NewGuid()}&model={voiceLiveModel}");
            var warmWs = new System.Net.WebSockets.ClientWebSocket();
            warmWs.Options.SetRequestHeader("Authorization", $"Bearer {freshToken.Token}");

            var warmStart = DateTime.UtcNow;
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await warmWs.ConnectAsync(wsUrl, connectCts.Token);
            var warmMs = (DateTime.UtcNow - warmStart).TotalMilliseconds;

            if (warmVoiceLiveSockets.TryAdd(contextId, (warmWs, DateTime.UtcNow)))
            {
                logger.LogInformation("Pre-warmed Voice Live WS in {WarmMs:F0}ms for context {ContextId}", warmMs, contextId);
            }
            else
            {
                // Race — another caller already won; dispose ours.
                await warmWs.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "duplicate", CancellationToken.None);
                warmWs.Dispose();
            }
        }
        catch (Exception warmEx)
        {
            logger.LogWarning(warmEx, "Voice Live pre-warm failed for context {ContextId} (will fall back to cold connect)", contextId);
        }
    });

    var target = new PhoneNumberIdentifier(request.PhoneNumber);
    var caller = new PhoneNumberIdentifier(acsPhoneNumber);

    var options = new CreateCallOptions(
        new CallInvite(target, caller),
        callbackUri)
    {
        MediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Mixed)
        {
            TransportUri = new Uri(websocketUri),
            MediaStreamingContent = MediaStreamingContent.Audio,
            StartMediaStreaming = true,
            EnableBidirectional = true,
            AudioFormat = AudioFormat.Pcm24KMono
        }
    };

    var result = await client.CreateCallAsync(options);
    var callConnectionId = result.Value.CallConnection.CallConnectionId;

    // Store the call connection ID so the WebSocket handler can hang up
    callConnections[contextId] = callConnectionId;

    logger.LogInformation("Outbound call initiated to {PhoneNumber}, ConnectionId={ConnectionId}",
        request.PhoneNumber, callConnectionId);

    telemetryClient.TrackEvent("OutboundCallInitiated", new Dictionary<string, string>
    {
        { "PhoneNumber", request.PhoneNumber },
        { "CallConnectionId", result.Value.CallConnection.CallConnectionId },
        { "Purpose", request.Purpose ?? "general" }
    });

    // Broadcast to call log subscribers
    var outboundEntry = new CallLogEntry(
        Direction: "outbound",
        PhoneNumber: request.PhoneNumber,
        Status: "ringing",
        ContextId: contextId,
        Name: request.Name,
        Purpose: request.Purpose,
        Timestamp: DateTimeOffset.UtcNow);
    callLogHistory.Add(outboundEntry);
    foreach (var sub in callLogSubscribers.Values)
    {
        sub.Writer.TryWrite(outboundEntry);
    }

    return Results.Ok(new
    {
        callConnectionId = result.Value.CallConnection.CallConnectionId,
        contextId,
        status = "ringing"
    });
});

// ---------------------------------------------------------------------------
// POST /api/incomingCall — Handle inbound calls via EventGrid
// ---------------------------------------------------------------------------
app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger,
    TelemetryClient telemetryClient) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        // Handle system events FIRST (validation doesn't need speed)
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                return Results.Ok(new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                });
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var callerId = Helper.GetCallerId(jsonObject);

        var inboundContextId = Guid.NewGuid().ToString();
        var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{inboundContextId}?callerId={callerId}");
        var websocketUri = appBaseUrl.Replace("https", "wss") + $"/ws?contextId={inboundContextId}";

        // Store caller phone number for telemetry correlation
        if (!string.IsNullOrEmpty(callerId))
        {
            callPhoneNumbers[inboundContextId] = callerId;
        }

        // Create a transcription channel for inbound calls too
        var inboundTranscriptionChannel = Channel.CreateUnbounded<TranscriptionEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        transcriptionChannels[inboundContextId] = inboundTranscriptionChannel;

        // Create an analysis channel for inbound calls too
        var inboundAnalysisChannel = Channel.CreateUnbounded<AnalysisResult>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        analysisChannels[inboundContextId] = inboundAnalysisChannel;

        // Force a fresh AI token before answering (same as outbound path).
        // ACS takes a few seconds to set up media streaming after answer,
        // so the token will still be valid when AzureVoiceLiveService uses it.
        await aiCredential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(
                scopes: new[] { "https://cognitiveservices.azure.com/.default" },
                claims: "{}"),
            CancellationToken.None);

        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            MediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Mixed)
            {
                TransportUri = new Uri(websocketUri),
                MediaStreamingContent = MediaStreamingContent.Audio,
                StartMediaStreaming = true,
                EnableBidirectional = true,
                AudioFormat = AudioFormat.Pcm24KMono
            }
        };

        var answerStartTime = DateTime.UtcNow;

        try
        {
            var answerCallResult = await client.AnswerCallAsync(options);
            var answerLatencyMs = (DateTime.UtcNow - answerStartTime).TotalMilliseconds;

            var inboundConnectionId = answerCallResult.Value.CallConnection.CallConnectionId;

            logger.LogInformation("[SUCCESS] Call answered in {LatencyMs:F0}ms. CallerId={CallerId}, ConnectionId={ConnectionId}",
                answerLatencyMs, callerId, inboundConnectionId);

            // Store the call connection ID so the WebSocket hang-up callback can disconnect the call
            callConnections[inboundContextId] = inboundConnectionId;

            // Broadcast inbound call to call log subscribers
            var inboundEntry = new CallLogEntry(
                Direction: "inbound",
                PhoneNumber: callerId ?? "Unknown",
                Status: "connected",
                ContextId: inboundContextId,
                Name: null,
                Purpose: null,
                Timestamp: DateTimeOffset.UtcNow);
            callLogHistory.Add(inboundEntry);
            foreach (var sub in callLogSubscribers.Values)
            {
                sub.Writer.TryWrite(inboundEntry);
            }

            telemetryClient.TrackEvent("CallAnsweredSuccess", new Dictionary<string, string>
            {
                { "CallConnectionId", inboundConnectionId },
                { "CallerId", callerId },
                { "AnswerLatencyMs", answerLatencyMs.ToString("F0") }
            });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 400 && ex.Message.Contains("8523"))
        {
            var latencyMs = (DateTime.UtcNow - answerStartTime).TotalMilliseconds;
            telemetryClient.TrackException(ex, new Dictionary<string, string>
            {
                { "Operation", "AnswerCall" },
                { "CallerId", callerId },
                { "ErrorCode", "8523_InvalidContext" },
                { "AnswerLatencyMs", latencyMs.ToString("F0") }
            });
            logger.LogError("[ERROR 8523] Token expired/invalid. Caller={CallerId}, LatencyMs={LatencyMs:F0}", callerId, latencyMs);
            throw;
        }
    }
    return Results.Ok();
});

// ---------------------------------------------------------------------------
// Callback handler for call automation events
// ---------------------------------------------------------------------------
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger,
    TelemetryClient telemetryClient) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation("Event received: {EventType} for context {ContextId}",
            @event.GetType().Name, contextId);

        telemetryClient.TrackEvent("CallAutomationEvent", new Dictionary<string, string>
        {
            { "EventType", @event.GetType().Name },
            { "ContextId", contextId },
            { "CallerId", callerId },
            { "CallConnectionId", @event.CallConnectionId ?? "N/A" }
        });

        // Clean up per-call prompt/language when the call ends
        if (@event is CallDisconnected)
        {
            // Read phone number BEFORE removing it (needed for call log entry below)
            callPhoneNumbers.TryRemove(contextId, out var disconnectedPhoneNumber);

            callPrompts.TryRemove(contextId, out _);
            callLanguages.TryRemove(contextId, out _);
            callLanguageCodes.TryRemove(contextId, out _);
            callTranscriptionHints.TryRemove(contextId, out _);
            callConnections.TryRemove(contextId, out _);

            // Complete the transcription channel so SSE consumers know the call ended
            if (transcriptionChannels.TryRemove(contextId, out var channel))
            {
                // Write a final "call ended" event before completing
                channel.Writer.TryWrite(new TranscriptionEvent("system", "Call ended", DateTime.UtcNow));
                channel.Writer.TryComplete();
            }

            // Complete the analysis channel
            if (analysisChannels.TryRemove(contextId, out var analysisChannel))
            {
                analysisChannel.Writer.TryComplete();
            }

            // Broadcast call-ended event to call log subscribers
            var endedEntry = new CallLogEntry(
                Direction: "ended",
                PhoneNumber: disconnectedPhoneNumber ?? "Unknown",
                Status: "disconnected",
                ContextId: contextId,
                Name: null,
                Purpose: null,
                Timestamp: DateTimeOffset.UtcNow);
            callLogHistory.Add(endedEntry);
            foreach (var sub in callLogSubscribers.Values)
            {
                sub.Writer.TryWrite(endedEntry);
            }

            logger.LogInformation("Cleaned up per-call data for context {ContextId}", contextId);
        }
    }

    return Results.Ok();
});

// ---------------------------------------------------------------------------
// WebSocket handler — ACS media streaming ↔ Azure OpenAI Realtime
// ---------------------------------------------------------------------------
app.UseWebSockets();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            try
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                var logger = context.RequestServices.GetRequiredService<ILogger<AcsMediaStreamingHandler>>();
                var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
                var telemetryClient = context.RequestServices.GetService<TelemetryClient>();

                // Look up per-call system prompt and language from the contextId query param.
                // IMPORTANT: Use TryGetValue (not TryRemove) because ACS opens TWO WebSocket
                // connections per call. TryRemove would consume the values on the first connection,
                // leaving the second (which carries the audio) with no prompt/language.
                // Cleanup happens on CallDisconnected instead.
                string? callSystemPrompt = null;
                string? callLanguage = null;
                string? callLanguageCode = null;
                string? callTranscriptionHint = null;
                string? callPhoneNumber = null;
                var wsContextId = context.Request.Query["contextId"].FirstOrDefault();
                if (!string.IsNullOrEmpty(wsContextId))
                {
                    if (callPrompts.TryGetValue(wsContextId, out var prompt))
                    {
                        callSystemPrompt = prompt;
                        logger.LogInformation("Using per-call system prompt for context {ContextId}", wsContextId);
                    }
                    if (callLanguages.TryGetValue(wsContextId, out var lang))
                    {
                        callLanguage = lang;
                        logger.LogInformation("Using per-call language '{Language}' for context {ContextId}", lang, wsContextId);
                    }
                    if (callLanguageCodes.TryGetValue(wsContextId, out var langCode))
                    {
                        callLanguageCode = langCode;
                        logger.LogInformation("Using per-call language code '{LanguageCode}' for context {ContextId}", langCode, wsContextId);
                    }
                    if (callTranscriptionHints.TryGetValue(wsContextId, out var hint))
                    {
                        callTranscriptionHint = hint;
                        logger.LogInformation("Using per-call transcription hint for context {ContextId}", wsContextId);
                    }
                    if (callPhoneNumbers.TryGetValue(wsContextId, out var phone))
                    {
                        callPhoneNumber = phone;
                    }
                }

                // Look up the transcription channel for this call
                ChannelWriter<TranscriptionEvent>? transcriptionWriter = null;
                if (!string.IsNullOrEmpty(wsContextId) && transcriptionChannels.TryGetValue(wsContextId, out var txChannel))
                {
                    transcriptionWriter = txChannel.Writer;
                }

                // Look up the analysis channel for this call
                ChannelWriter<AnalysisResult>? analysisWriter = null;
                if (!string.IsNullOrEmpty(wsContextId) && analysisChannels.TryGetValue(wsContextId, out var axChannel))
                {
                    analysisWriter = axChannel.Writer;
                }

                // Claim the pre-warmed Voice Live WebSocket if one was set up for this
                // contextId during /api/outboundCall. ACS opens TWO media WebSockets per
                // call — TryRemove ensures only the first one consumes the warm socket;
                // the second falls through to a cold connect (its latency doesn't matter,
                // it's the duplicate one). If the warm socket is no longer Open (timed out
                // or callee never picked up), AzureVoiceLiveService falls back to cold.
                System.Net.WebSockets.ClientWebSocket? warmVoiceLiveWs = null;
                if (!string.IsNullOrEmpty(wsContextId) && warmVoiceLiveSockets.TryRemove(wsContextId, out var warmEntry))
                {
                    warmVoiceLiveWs = warmEntry.Ws;
                    logger.LogInformation("Claimed pre-warmed Voice Live WS for context {ContextId} (age: {AgeMs:F0}ms, state: {State})",
                        wsContextId, (DateTime.UtcNow - warmEntry.CreatedAt).TotalMilliseconds, warmVoiceLiveWs.State);
                }

                var mediaService = new AcsMediaStreamingHandler(
                    webSocket,
                    builder.Configuration,
                    logger,
                    loggerFactory,
                    aiCredential,
                    callSystemPrompt,
                    callLanguage,
                    callLanguageCode,
                    callTranscriptionHint,
                    telemetryClient,
                    callPhoneNumber,
                    transcriptionWriter,
                    analysisWriter,
                    warmVoiceLiveWs);

                // Register hang-up callback so the AI can disconnect the call
                if (!string.IsNullOrEmpty(wsContextId))
                {
                    mediaService.OnHangUp(async (reason) =>
                    {
                        if (callConnections.TryRemove(wsContextId, out var connId))
                        {
                            try
                            {
                                logger.LogInformation("Hanging up call {ConnectionId}. Reason: {Reason}", connId, reason);
                                await client.GetCallConnection(connId).HangUpAsync(true);
                                logger.LogInformation("Call {ConnectionId} disconnected successfully", connId);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Failed to hang up call {ConnectionId}", connId);
                                telemetryClient?.TrackException(ex, new Dictionary<string, string>
                                {
                                    { "Component", "HangUpCallback" },
                                    { "CallConnectionId", connId },
                                    { "ContextId", wsContextId }
                                });
                            }
                        }
                        else
                        {
                            logger.LogWarning("No call connection found for context {ContextId} — call may already be disconnected", wsContextId);
                        }
                    });
                }

                await mediaService.ProcessWebSocketAsync();
            }
            catch (Exception ex)
            {
                var wsLogger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                var wsTelemetry = context.RequestServices.GetService<TelemetryClient>();
                wsLogger.LogError(ex, "WebSocket processing exception");
                wsTelemetry?.TrackException(ex, new Dictionary<string, string>
                {
                    { "ContextId", context.Request.Query["contextId"].FirstOrDefault() ?? "unknown" },
                    { "Component", "WebSocketHandler" }
                });
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});

// ---------------------------------------------------------------------------
// GET /api/transcription/{contextId} — SSE stream of live transcription events
// ---------------------------------------------------------------------------
app.MapGet("/api/transcription/{contextId}", async (string contextId, HttpContext httpContext, ILogger<Program> logger) =>
{
    if (!transcriptionChannels.TryGetValue(contextId, out var channel))
    {
        httpContext.Response.StatusCode = 404;
        await httpContext.Response.WriteAsync("Call not found or already ended");
        return;
    }

    httpContext.Response.Headers["Content-Type"] = "text/event-stream";
    httpContext.Response.Headers["Cache-Control"] = "no-cache";
    httpContext.Response.Headers["Connection"] = "keep-alive";
    httpContext.Response.Headers["X-Accel-Buffering"] = "no";
    await httpContext.Response.Body.FlushAsync();

    logger.LogInformation("SSE transcription client connected for context {ContextId}", contextId);

    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(httpContext.RequestAborted))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(evt);
            await httpContext.Response.WriteAsync($"data: {json}\n\n", httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }

        // Channel completed — send final event
        await httpContext.Response.WriteAsync("data: [DONE]\n\n");
        await httpContext.Response.Body.FlushAsync();
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("SSE transcription client disconnected for context {ContextId}", contextId);
    }
});

// ---------------------------------------------------------------------------
// GET /api/analysis/{contextId} — SSE stream of live conversation analysis
// ---------------------------------------------------------------------------
app.MapGet("/api/analysis/{contextId}", async (string contextId, HttpContext httpContext, ILogger<Program> logger) =>
{
    if (!analysisChannels.TryGetValue(contextId, out var channel))
    {
        httpContext.Response.StatusCode = 404;
        await httpContext.Response.WriteAsync("Call not found or already ended");
        return;
    }

    httpContext.Response.Headers["Content-Type"] = "text/event-stream";
    httpContext.Response.Headers["Cache-Control"] = "no-cache";
    httpContext.Response.Headers["Connection"] = "keep-alive";
    httpContext.Response.Headers["X-Accel-Buffering"] = "no";
    await httpContext.Response.Body.FlushAsync();

    logger.LogInformation("SSE analysis client connected for context {ContextId}", contextId);

    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(httpContext.RequestAborted))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(evt);
            await httpContext.Response.WriteAsync($"data: {json}\n\n", httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }

        // Channel completed — send final event
        await httpContext.Response.WriteAsync("data: [DONE]\n\n");
        await httpContext.Response.Body.FlushAsync();
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("SSE analysis client disconnected for context {ContextId}", contextId);
    }
});

// ---------------------------------------------------------------------------
// GET /api/calls/stream — SSE stream of all call log events
// ---------------------------------------------------------------------------
app.MapGet("/api/calls/stream", async (HttpContext httpContext, ILogger<Program> logger) =>
{
    httpContext.Response.Headers["Content-Type"] = "text/event-stream";
    httpContext.Response.Headers["Cache-Control"] = "no-cache";
    httpContext.Response.Headers["Connection"] = "keep-alive";
    httpContext.Response.Headers["X-Accel-Buffering"] = "no";
    await httpContext.Response.Body.FlushAsync();

    var subscriberId = Guid.NewGuid().ToString();
    var subscriberChannel = Channel.CreateUnbounded<CallLogEntry>();
    callLogSubscribers[subscriberId] = subscriberChannel;

    logger.LogInformation("SSE call log client {SubscriberId} connected", subscriberId);

    try
    {
        // Only replay calls that are still active (not yet ended)
        var endedContextIds = callLogHistory
            .Where(h => h.Status == "disconnected")
            .Select(h => h.ContextId)
            .ToHashSet();

        foreach (var historic in callLogHistory.OrderBy(m => m.Timestamp))
        {
            // Skip calls that have already ended
            if (endedContextIds.Contains(historic.ContextId)) continue;

            var histJson = System.Text.Json.JsonSerializer.Serialize(historic);
            await httpContext.Response.WriteAsync($"data: {histJson}\n\n", httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }

        // Then stream live events as they arrive
        await foreach (var entry in subscriberChannel.Reader.ReadAllAsync(httpContext.RequestAborted))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(entry);
            await httpContext.Response.WriteAsync($"data: {json}\n\n", httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("SSE call log client {SubscriberId} disconnected", subscriberId);
    }
    finally
    {
        // Remove subscriber when client disconnects
        callLogSubscribers.TryRemove(subscriberId, out _);
    }
});

// ---------------------------------------------------------------------------
// GET /api/calls/history — Get all call log events (non-streaming)
// ---------------------------------------------------------------------------
app.MapGet("/api/calls/history", (ILogger<Program> logger) =>
{
    // Only return calls that are still active (no "disconnected" event yet)
    var endedContextIds = callLogHistory
        .Where(h => h.Status == "disconnected")
        .Select(h => h.ContextId)
        .ToHashSet();

    var active = callLogHistory
        .Where(h => !endedContextIds.Contains(h.ContextId))
        .OrderBy(m => m.Timestamp)
        .ToArray();

    logger.LogInformation("Call log history requested, returning {Count} active entries (of {Total} total)", active.Length, callLogHistory.Count);
    return Results.Ok(active);
});

// Background cleanup: drop pre-warmed Voice Live WebSockets that were never claimed
// (callee never picked up, ACS never opened the media WS, etc.). Voice Live closes
// idle WS at ~60s anyway, but actively closing ours frees the connection sooner
// and keeps the dictionary bounded.
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        var now = DateTime.UtcNow;
        foreach (var key in warmVoiceLiveSockets.Keys.ToArray())
        {
            if (warmVoiceLiveSockets.TryGetValue(key, out var entry) &&
                (now - entry.CreatedAt) > TimeSpan.FromMinutes(2))
            {
                if (warmVoiceLiveSockets.TryRemove(key, out var stale))
                {
                    try
                    {
                        if (stale.Ws.State == System.Net.WebSockets.WebSocketState.Open)
                        {
                            await stale.Ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "unclaimed", CancellationToken.None);
                        }
                    }
                    catch { /* best effort */ }
                    finally
                    {
                        stale.Ws.Dispose();
                    }
                }
            }
        }
    }
});

app.Run();

record OutboundCallRequest(string PhoneNumber, string? Purpose, string? SystemPrompt, string? Name, string? Language, string? LanguageCode, string? TranscriptionHint);
public record TranscriptionEvent(string Speaker, string Text, DateTime Timestamp);
public record CallLogEntry(string Direction, string PhoneNumber, string Status, string ContextId, string? Name, string? Purpose, DateTimeOffset Timestamp);