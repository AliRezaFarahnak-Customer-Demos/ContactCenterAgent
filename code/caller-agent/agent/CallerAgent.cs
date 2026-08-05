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
using CallerAgent.Outreach;

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
    var voiceLiveModel = builder.Configuration.GetValue<string>("AzureOpenAI:DeploymentName") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.Model;
    var probeApiVersion = builder.Configuration.GetValue<string>("AzureOpenAI:ApiVersion") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.ApiVersion;
    var probeByomProfile = builder.Configuration.GetValue<string>("AzureOpenAI:ByomProfile") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.ByomProfile;
    if (!string.IsNullOrEmpty(voiceLiveEndpoint))
    {
        // Must mirror AzureVoiceLiveService's URL exactly, or the probe validates a
        // different model than the one calls actually use.
        var probeProfileParam = string.IsNullOrWhiteSpace(probeByomProfile) ? "" : $"&profile={probeByomProfile}";
        var wsUrl = new Uri($"{voiceLiveEndpoint.TrimEnd('/').Replace("https", "wss")}/voice-live/realtime?api-version={probeApiVersion}&x-ms-client-request-id={Guid.NewGuid()}&model={voiceLiveModel}{probeProfileParam}");
        using var probeWs = new System.Net.WebSockets.ClientWebSocket();
        probeWs.Options.SetRequestHeader("Authorization", $"Bearer {tokenResult.Token}");
        using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await probeWs.ConnectAsync(wsUrl, probeCts.Token);
        await probeWs.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "startup probe", CancellationToken.None);
        Console.WriteLine($"✓ Voice Live WebSocket probe OK (model: {voiceLiveModel}, api-version: {probeApiVersion}, profile: {(string.IsNullOrWhiteSpace(probeByomProfile) ? "(none)" : probeByomProfile)})");
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
var callVoices = new ConcurrentDictionary<string, string>();
var callVoiceStyles = new ConcurrentDictionary<string, string>();
var callPhoneNumbers = new ConcurrentDictionary<string, string>();
// Track active call connection IDs (keyed by WebSocket contextId)
var callConnections = new ConcurrentDictionary<string, string>();
// Live transcription channels (keyed by contextId) — SSE consumers read from these
var transcriptionChannels = new ConcurrentDictionary<string, Channel<TranscriptionEvent>>();
// Live analysis channels (keyed by contextId) — SSE consumers read analysis results
var analysisChannels = new ConcurrentDictionary<string, Channel<AnalysisResult>>();
// Case-summary channels (keyed by contextId) — one structured summary per call, drained by the outreach finalizer
var caseSummaryChannels = new ConcurrentDictionary<string, Channel<CaseSummary>>();

// Global call log — subscriber channels for broadcasting call events to multiple SSE clients
var callLogSubscribers = new ConcurrentDictionary<string, Channel<CallLogEntry>>();
// Keep a history so late-connecting SSE clients see recent entries
var callLogHistory = new ConcurrentBag<CallLogEntry>();

// ---------------------------------------------------------------------------
// Outreach services (voice + SMS + email) + MCP server (anonymous)
// ---------------------------------------------------------------------------
builder.Services.AddSingleton(sp => new OutreachStore(builder.Configuration, aiCredential, sp.GetRequiredService<ILogger<OutreachStore>>()));
builder.Services.AddSingleton(sp => new OutreachService(
    sp.GetRequiredService<OutreachStore>(), builder.Configuration,
    sp.GetRequiredService<ILogger<OutreachService>>(), sp.GetService<TelemetryClient>()));
// Captures email replies from a mailbox when Graph:SenderAddress is set; no-op otherwise.
builder.Services.AddHostedService<GraphInboxPoller>();
builder.Services.AddHostedService<CallbackDispatcher>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<OutreachTools>();

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
    var contextId = await PlaceOutboundCallCoreAsync(request, logger, telemetryClient);
    return Results.Ok(new { contextId, status = "ringing" });
});

// Core outbound-call placement, shared by /api/outboundCall and the outreach VoicePlacer.
// Returns the callback/WebSocket contextId.
async Task<string> PlaceOutboundCallCoreAsync(OutboundCallRequest request, ILogger logger, TelemetryClient telemetryClient)
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
    if (!string.IsNullOrEmpty(request.Voice))
    {
        callVoices[contextId] = request.Voice;
    }
    // Empty string is meaningful here ("no style"), so only a null skips the override.
    if (request.VoiceStyle is not null)
    {
        callVoiceStyles[contextId] = request.VoiceStyle;
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

    // Case-summary channel + finalizer: enriches the outreach record (if any) with the
    // structured summary the voice service emits at teardown. Times out for non-outreach calls.
    var caseSummaryChannel = Channel.CreateUnbounded<CaseSummary>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });
    caseSummaryChannels[contextId] = caseSummaryChannel;
    var outreachForVoice = app.Services.GetRequiredService<OutreachService>();
    _ = Task.Run(async () =>
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await foreach (var summary in caseSummaryChannel.Reader.ReadAllAsync(cts.Token))
            {
                await outreachForVoice.FinalizeVoiceAsync(contextId, summary);
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Case summary finalizer failed for {ContextId}", contextId); }
        finally { caseSummaryChannels.TryRemove(contextId, out _); }
    });

    // Force a fresh token before placing the call. Passing claims: "{}" bypasses
    // Azure.Core's token cache so we always get a brand-new token. ACS takes
    // several seconds to dial, so the token will still be valid when the
    // WebSocket connects and AzureVoiceLiveService uses it.
    await aiCredential.GetTokenAsync(
        new Azure.Core.TokenRequestContext(
            scopes: new[] { "https://cognitiveservices.azure.com/.default" },
            claims: "{}"),
        CancellationToken.None);

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
        { "CallConnectionId", callConnectionId },
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

    return contextId;
}

// Wire the voice channel into the outreach orchestrator (ACS call state stays here).
app.Services.GetRequiredService<OutreachService>().VoicePlacer = async (record) =>
{
    var ctx = record.Context ?? new Dictionary<string, string>();
    var req = new OutboundCallRequest(
        record.Phone ?? "",
        record.Intent ?? "outreach",
        record.Message,
        record.CustomerName,
        "Danish",
        "da",
        null,
        ctx.TryGetValue("voice", out var v) ? v : null,
        ctx.TryGetValue("voiceStyle", out var vs) ? vs : null);
    var vpLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VoicePlacer");
    var vpTelemetry = app.Services.GetRequiredService<TelemetryClient>();
    return await PlaceOutboundCallCoreAsync(req, vpLogger, vpTelemetry);
};

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
    OutreachService outreach,
    TelemetryClient telemetryClient) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation("Event received: {EventType} for context {ContextId}",
            @event.GetType().Name, contextId);

        // Surface the ACS failure reason for CreateCallFailed (and any other failure events).
        // Without this we only see the event name and have no idea WHY ACS rejected the call.
        if (@event is CreateCallFailed createCallFailed)
        {
            var ri = createCallFailed.ResultInformation;
            logger.LogError(
                "CreateCallFailed for context {ContextId}: code={Code}, subCode={SubCode}, message={Message}",
                contextId, ri?.Code, ri?.SubCode, ri?.Message);
            telemetryClient.TrackEvent("CreateCallFailedDetail", new Dictionary<string, string>
            {
                { "ContextId", contextId },
                { "CallerId", callerId },
                { "ResultCode", ri?.Code.ToString() ?? "" },
                { "ResultSubCode", ri?.SubCode.ToString() ?? "" },
                { "ResultMessage", ri?.Message ?? "" }
            });
        }

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
            callVoices.TryRemove(contextId, out _);
            callVoiceStyles.TryRemove(contextId, out _);
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

            // Mark any linked outreach as completed (the voice case summary enriches it
            // separately, off this path, when the voice service emits it at teardown).
            _ = outreach.FinalizeVoiceAsync(contextId, null);
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
                string? callVoice = null;
                string? callVoiceStyle = null;
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
                    if (callVoices.TryGetValue(wsContextId, out var voice))
                    {
                        callVoice = voice;
                        logger.LogInformation("Using per-call voice '{Voice}' for context {ContextId}", voice, wsContextId);
                    }
                    if (callVoiceStyles.TryGetValue(wsContextId, out var voiceStyle))
                    {
                        callVoiceStyle = voiceStyle;
                        logger.LogInformation("Using per-call voice style '{Style}' for context {ContextId}", string.IsNullOrEmpty(voiceStyle) ? "(none)" : voiceStyle, wsContextId);
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

                // Look up the case-summary channel for this call
                ChannelWriter<CaseSummary>? caseSummaryWriter = null;
                if (!string.IsNullOrEmpty(wsContextId) && caseSummaryChannels.TryGetValue(wsContextId, out var csChannel))
                {
                    caseSummaryWriter = csChannel.Writer;
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
                    caseSummaryWriter,
                    callVoice,
                    callVoiceStyle);

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

// ---------------------------------------------------------------------------
// Outreach — unified voice / SMS / email surface (REST). Mirrored by MCP tools.
// ---------------------------------------------------------------------------
app.MapPost("/api/outreach", async ([FromBody] OutreachRequest request, OutreachService outreach) =>
{
    try
    {
        return Results.Ok(await outreach.StartAsync(request));
    }
    catch (OutreachConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

app.MapGet("/api/outreach", async (int? limit, OutreachService outreach) =>
    Results.Ok(await outreach.ListRecentAsync(limit ?? 50)));

app.MapGet("/api/outreach/{id}", async (string id, OutreachService outreach) =>
{
    var result = await outreach.GetResultAsync(id);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/outreach/{id}/followup", async (string id, [FromBody] FollowUpRequest req, OutreachService outreach) =>
{
    if (string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "message is required" });
    var result = await outreach.SendFollowUpAsync(id, req.Message, req.Channel, req.Subject, req.ClientRequestId);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/outreach/{id}/callback/retry", async (string id, OutreachService outreach, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await outreach.RetryCallbackAsync(id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/personas", (OutreachService outreach) => Results.Ok(outreach.ListPersonas()));

// Demo reset: wipe every outreach/customer session. Disable with Outreach:AllowDataReset=false.
app.MapDelete("/api/outreach", async (OutreachService outreach, ILogger<Program> logger) =>
{
    if (app.Configuration.GetValue<bool?>("Outreach:AllowDataReset") == false)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var deleted = await outreach.ClearAllAsync();
    logger.LogInformation("Outreach data reset: {Count} records deleted", deleted);
    return Results.Ok(new { deleted });
});

// Demo/testing aid: inject a customer "reply" into a thread using the SAME inbound code path as a real
// Event Grid reply. Lets the dashboard show a two-way thread when a live carrier inbound isn't available
// (e.g. trial test sender). Disable in production with Outreach:AllowSimulatedReplies=false.
app.MapPost("/api/outreach/simulate-reply", async ([FromBody] SimulateReplyRequest req, OutreachService outreach) =>
{
    if (app.Configuration.GetValue<bool?>("Outreach:AllowSimulatedReplies") == false)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(req.From) || string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "from and message are required" });
    var channel = string.IsNullOrWhiteSpace(req.Channel) ? "sms" : req.Channel!.Trim().ToLowerInvariant();
    if (channel == "email")
        await outreach.HandleInboundEmailAsync(req.From, req.Subject ?? "", req.Message);
    else
        await outreach.HandleInboundSmsAsync(req.From, req.Message);
    return Results.Ok(new { ok = true, channel });
});

app.MapGet("/api/channels", (OutreachService outreach) => Results.Ok(outreach.ListChannels()));

app.MapGet("/api/customers", async (OutreachService outreach) =>
    Results.Ok(await outreach.ListCustomersAsync()));

app.MapGet("/api/customers/{customerId}/timeline", async (string customerId, OutreachService outreach) =>
    Results.Ok(await outreach.GetCustomerTimelineAsync(customerId)));

// ---------------------------------------------------------------------------
// Inbound reply webhooks (Event Grid). Anonymous; validate handshake on creation.
// ---------------------------------------------------------------------------
app.MapPost("/api/events/sms", async ([FromBody] EventGridEvent[] events, OutreachService outreach, ILogger<Program> logger) =>
{
    foreach (var e in events)
    {
        if (e.TryGetSystemEventData(out var data))
        {
            if (data is SubscriptionValidationEventData validation)
                return Results.Ok(new { validationResponse = validation.ValidationCode });
            if (data is AcsSmsReceivedEventData sms)
            {
                logger.LogInformation("Inbound SMS from {From}", sms.From);
                await outreach.HandleInboundSmsAsync(sms.From, sms.Message, e.Id);
            }
        }
    }
    return Results.Ok();
});

app.MapPost("/api/events/email", async ([FromBody] EventGridEvent[] events, OutreachService outreach, ILogger<Program> logger) =>
{
    foreach (var e in events)
    {
        if (e.TryGetSystemEventData(out var data) && data is SubscriptionValidationEventData validation)
            return Results.Ok(new { validationResponse = validation.ValidationCode });
        try
        {
            var payload = e.Data.ToObjectFromJson<Dictionary<string, object>>();
            var from = payload.GetValueOrDefault("from")?.ToString() ?? payload.GetValueOrDefault("sender")?.ToString() ?? "";
            var subject = payload.GetValueOrDefault("subject")?.ToString() ?? "";
            var body = payload.GetValueOrDefault("plainText")?.ToString() ?? payload.GetValueOrDefault("body")?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(from))
            {
                logger.LogInformation("Inbound email from {From}", from);
                await outreach.HandleInboundEmailAsync(from, subject, body, e.Id);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to parse inbound email event"); }
    }
    return Results.Ok();
});

// ---------------------------------------------------------------------------
// MCP server — anonymous, Streamable HTTP at /mcp (frontend + local VS Code).
// ---------------------------------------------------------------------------
app.MapMcp("/mcp");

app.Run();

record OutboundCallRequest(string PhoneNumber, string? Purpose, string? SystemPrompt, string? Name, string? Language, string? LanguageCode, string? TranscriptionHint, string? Voice, string? VoiceStyle);
record FollowUpRequest(string Message, string? Channel = null, string? Subject = null, string? ClientRequestId = null);
public record TranscriptionEvent(string Speaker, string Text, DateTime Timestamp);
public record CallLogEntry(string Direction, string PhoneNumber, string Status, string ContextId, string? Name, string? Purpose, DateTimeOffset Timestamp);