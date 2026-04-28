using System.Net.WebSockets;
using Azure.Communication.CallAutomation;
using Azure.Identity;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive
{
    /// <summary>
    /// Azure Voice Live Service — bridges ACS media stream to Azure Voice Live API
    /// via raw WebSocket. Authenticates with Managed Identity (Azure) or AzureCliCredential (local).
    /// Uses azure_semantic_vad, deep noise suppression, echo cancellation, and HD voice.
    /// </summary>
    public class AzureVoiceLiveService
    {
        private CancellationTokenSource m_cts;
        private AcsMediaStreamingHandler m_mediaStreaming;

        // Fallback prompt used ONLY when no per-call prompt is supplied (e.g. inbound calls
        // not initiated through admin-chat). For outbound calls placed via /api/place-call,
        // the full system prompt comes from the editable persona in the admin-chat backend
        // and is sent here VERBATIM — no rules are appended.
        private string m_systemPrompt =
            "You are a kind, friendly Norlys phone assistant. Speak naturally in the caller's language. " +
            "Keep replies conversational — typically 1–3 sentences. Ask one question at a time and wait for the answer. " +
            "Never say you are an AI; if asked, say 'den digitale assistent'. " +
            "Only end the call with hang_up after the caller has explicitly said goodbye.";


        private ClientWebSocket m_azureVoiceLiveWebsocket = null!;
        private readonly ILogger<AzureVoiceLiveService> m_logger;
        private readonly string? m_language;
        private readonly string? m_languageCode;
        private readonly string? m_transcriptionHint;
        private readonly Azure.Core.TokenCredential m_credential;
        private readonly TelemetryClient? m_telemetryClient;
        private readonly string? m_phoneNumber;
        private Func<string, Task>? m_onHangUp;
        private bool m_pendingHangUp;
        private string? m_pendingHangUpReason;
        private readonly ChannelWriter<TranscriptionEvent>? m_transcriptionWriter;
        private readonly ChannelWriter<AnalysisResult>? m_analysisWriter;
        private readonly IConfiguration m_configuration;
        private ConversationAnalysisService? m_analysisService;

        // Cached JSON options — avoid allocation on every audio frame
        private static readonly JsonSerializerOptions s_compactJson = new() { WriteIndented = false };

        /// <summary>
        /// Register a callback to be invoked when the AI decides to hang up the call.
        /// The callback receives a reason string.
        /// </summary>
        public void OnHangUp(Func<string, Task> callback) => m_onHangUp = callback;

        public AzureVoiceLiveService(
            AcsMediaStreamingHandler mediaStreaming,
            IConfiguration configuration,
            ILogger<AzureVoiceLiveService> logger,
            Azure.Core.TokenCredential credential,
            string? callSystemPrompt = null,
            string? callLanguage = null,
            string? callLanguageCode = null,
            string? callTranscriptionHint = null,
            TelemetryClient? telemetryClient = null,
            string? phoneNumber = null,
            ChannelWriter<TranscriptionEvent>? transcriptionWriter = null,
            ChannelWriter<AnalysisResult>? analysisWriter = null)
        {
            m_mediaStreaming = mediaStreaming;
            m_configuration = configuration;
            m_cts = new CancellationTokenSource();
            m_logger = logger;
            m_credential = credential;
            m_telemetryClient = telemetryClient;
            m_phoneNumber = phoneNumber;
            m_language = callLanguage;
            m_languageCode = callLanguageCode;
            m_transcriptionHint = callTranscriptionHint;
            m_transcriptionWriter = transcriptionWriter;
            m_analysisWriter = analysisWriter;

            // Use per-call system prompt VERBATIM if provided. The admin-chat backend is the
            // single source of truth for the prompt — we don't append, prepend, or modify.
            if (!string.IsNullOrEmpty(callSystemPrompt))
            {
                m_systemPrompt = callSystemPrompt;
                m_logger.LogInformation("Using per-call system prompt verbatim ({Length} chars, preview: {Prompt})",
                    callSystemPrompt.Length, callSystemPrompt[..Math.Min(80, callSystemPrompt.Length)]);
            }

            m_logger.LogInformation("AzureVoiceLiveService initialized");
        }

        /// <summary>
        /// Initialize the AI session. Must be called (and awaited) before audio flows.
        /// Separated from constructor to avoid sync-over-async blocking.
        /// Retries once with a force-refreshed token on 401 (stale token after deployment restart).
        /// </summary>
        public async Task InitializeAsync(IConfiguration configuration)
        {
            try
            {
                var azureVoiceLiveEndpoint = configuration.GetValue<string>("AzureOpenAI:Endpoint");
                ArgumentNullException.ThrowIfNullOrEmpty(azureVoiceLiveEndpoint);

                var voiceLiveModel = configuration.GetValue<string>("AzureOpenAI:DeploymentName") ?? "gpt-realtime";

                m_logger.LogInformation("Connecting to Azure Voice Live: {Endpoint}, model: {Model}", azureVoiceLiveEndpoint, voiceLiveModel);

                var azureVoiceLiveWebsocketUrl = new Uri(
                    $"{azureVoiceLiveEndpoint.TrimEnd('/').Replace("https", "wss")}/voice-live/realtime?api-version=2025-10-01&x-ms-client-request-id={Guid.NewGuid()}&model={voiceLiveModel}");

                // Try connecting with the cached token first. If we get a 401 (stale token
                // after container restart / deployment), force-refresh and retry once.
                const int MaxAttempts = 2;
                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    var forceRefresh = attempt > 1;
                    var tokenContext = forceRefresh
                        ? new Azure.Core.TokenRequestContext(
                              scopes: new[] { "https://cognitiveservices.azure.com/.default" },
                              claims: "{}")   // bypass MSAL cache
                        : new Azure.Core.TokenRequestContext(
                              new[] { "https://cognitiveservices.azure.com/.default" });

                    var tokenResult = await m_credential.GetTokenAsync(tokenContext, CancellationToken.None);
                    m_logger.LogInformation("Token acquired (attempt {Attempt}, forceRefresh: {Force}, expires: {Expiry})",
                        attempt, forceRefresh, tokenResult.ExpiresOn.ToString("HH:mm:ss"));

                    m_azureVoiceLiveWebsocket = new ClientWebSocket();
                    m_azureVoiceLiveWebsocket.Options.SetRequestHeader("Authorization", $"Bearer {tokenResult.Token}");

                    try
                    {
                        m_logger.LogInformation("Connecting to {Url} (attempt {Attempt})...", azureVoiceLiveWebsocketUrl, attempt);
                        await m_azureVoiceLiveWebsocket.ConnectAsync(azureVoiceLiveWebsocketUrl, CancellationToken.None);
                        m_logger.LogInformation("Connected successfully on attempt {Attempt}!", attempt);
                        break; // success — exit retry loop
                    }
                    catch (WebSocketException ex) when (
                        attempt < MaxAttempts &&
                        ex.Message.Contains("401"))
                    {
                        m_logger.LogWarning("WebSocket connect got 401 on attempt {Attempt}, retrying with force-refreshed token...", attempt);
                        m_azureVoiceLiveWebsocket.Dispose();
                        continue; // retry with force-refresh
                    }
                }

                // Start listening for messages
                StartConversation();

                // Initialize conversation analysis service (uses gpt-5.4-nano structured outputs)
                if (m_analysisWriter != null)
                {
                    try
                    {
                        var analysisLoggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
                        var analysisLogger = analysisLoggerFactory.CreateLogger<ConversationAnalysisService>();
                        m_analysisService = new ConversationAnalysisService(
                            configuration, analysisLogger, m_credential, m_analysisWriter, m_telemetryClient);
                        m_logger.LogInformation("Conversation analysis service initialized");
                    }
                    catch (Exception analysisEx)
                    {
                        m_logger.LogWarning(analysisEx, "Failed to initialize analysis service — analysis will be skipped");
                    }
                }

                // Update session with Voice Live settings
                await UpdateSessionAsync();

                // Start response from AI
                await StartResponseAsync();

                m_logger.LogInformation("Voice Live session fully initialized and ready");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Error during Voice Live initialization");
                m_telemetryClient?.TrackException(ex, new Dictionary<string, string>
                {
                    { "Component", "AzureVoiceLiveService.InitializeAsync" }
                });
                throw;
            }
        }

        private async Task UpdateSessionAsync()
        {
            // Build effective system prompt: persona + hang-up rules (always) + language
            var effectivePrompt = m_systemPrompt;

            // Language instruction — forces the AI to speak in the specified language
            if (!string.IsNullOrEmpty(m_language) && !m_systemPrompt.Contains(m_language, StringComparison.OrdinalIgnoreCase))
            {
                effectivePrompt += $@"
CRITICAL LANGUAGE RULE — READ CAREFULLY:
- You MUST speak ONLY in {m_language} for the ENTIRE duration of this phone call.
- This applies to EVERY utterance: your opening greeting, all answers, follow-up questions, and your farewell before hanging up.
- Do NOT mix languages. Do NOT fall back to any other language even if the other person's accent or words sound similar to another language.
- If the specified language is Danish: speak Danish (dansk). Danish is NOT English. Use Danish vocabulary, Danish grammar, and Danish pronunciation. Example greeting: 'Hej, hvad kan jeg hjælpe med?' — NOT 'Hey, how can I help?'.
- If the specified language is English: speak English only. Do NOT speak Danish, Swedish, Norwegian, or any other Scandinavian language, even if the caller has a Scandinavian accent.
- If the caller responds in a different language than {m_language}, continue speaking {m_language} unless they explicitly ask you to switch.
- Your goodbye before hanging up MUST also be in {m_language}.";
                m_logger.LogInformation("Language instruction appended to system prompt: {Language}", m_language);
            }

            // Detect Danish (or any non-English-family language) so we can tune VAD/transcription accordingly.
            // azure_semantic_vad_multilingual officially supports EN/ES/FR/IT/DE/JA/PT/ZH/KO/HI — Danish falls back.
            // The English-only filler-word remover (remove_filler_words) adds latency without benefit for Danish callers.
            var isEnglish = string.IsNullOrEmpty(m_languageCode)
                || m_languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);

            // ---------------------------------------------------------------------
            // VAD CONFIG — kept identical to danish-voice-lab.
            //   azure_semantic_vad, threshold 0.3, prefix 300ms, silence 500ms.
            // The English-vs-other branching for silence_duration_ms is preserved
            // as a knob, but defaults to 500ms (same as the lab) for both. The
            // older PSTN-tuned 0.7/400/900 values caused the phone to behave
            // differently from the console sandbox the user has tuned by ear.
            // All knobs live under "VoiceLive:Vad" in appsettings.json.
            // ---------------------------------------------------------------------
            var vadType = m_configuration.GetValue<string>("VoiceLive:Vad:Type") ?? "azure_semantic_vad";
            var vadThreshold = m_configuration.GetValue<double>("VoiceLive:Vad:Threshold", 0.3);
            var vadPrefixPaddingMs = m_configuration.GetValue<int>("VoiceLive:Vad:PrefixPaddingMs", 300);
            var vadSilenceMs = m_configuration.GetValue<int>(
                isEnglish ? "VoiceLive:Vad:SilenceDurationMsEnglish" : "VoiceLive:Vad:SilenceDurationMsOther",
                500);

            m_logger.LogInformation(
                "VAD config: type={Type}, threshold={Threshold}, prefix={Prefix}ms, silence={Silence}ms, language={Lang}",
                vadType, vadThreshold, vadPrefixPaddingMs, vadSilenceMs, m_languageCode ?? "(auto)");

            var jsonObject = new
            {
                type = "session.update",
                session = new
                {
                    // Pin the contract explicitly so future server-side default changes can't drift on us.
                    // gpt-realtime-1.5 supports both modalities; ACS streams 16-bit PCM mono → pcm16 both ways.
                    modalities = new[] { "audio", "text" },
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",
                    instructions = effectivePrompt,
                    // EXACT MIRROR of danish-voice-lab: only these three VAD fields are sent.
                    // Server defaults handle interrupt_response (true), remove_filler_words (off),
                    // and auto_truncate. Adding extra fields was causing wire-payload drift vs
                    // the console sandbox.
                    turn_detection = new
                    {
                        type = vadType,
                        threshold = vadThreshold,
                        prefix_padding_ms = vadPrefixPaddingMs,
                        silence_duration_ms = vadSilenceMs
                    },
                    // No max_response_output_tokens cap — danish-voice-lab doesn't set one and
                    // we want identical behaviour. The system prompt's "1-2 sentences" rule
                    // is the soft constraint instead.
                    input_audio_noise_reduction = new { type = "azure_deep_noise_suppression" },
                    input_audio_echo_cancellation = new { type = "server_echo_cancellation" },
                    input_audio_transcription = BuildTranscriptionConfig(m_configuration, m_languageCode, m_transcriptionHint, m_logger),
                    voice = BuildVoiceConfig(m_configuration, m_logger),
                    tools = new[]
                    {
                        new
                        {
                            type = "function",
                            name = "hang_up",
                            description = "End the phone call. Call this tool whenever the caller signals they want to end the conversation — in ANY language and ANY phrasing. " +
                                          "This includes explicit farewells ('bye', 'goodbye', 'hej hej', 'farvel', 'have a good day', 'take care', 'see you'), " +
                                          "polite wrap-ups ('thanks, that's all', 'tak, det var det', 'no, nothing more', 'nej det var det', 'okay vi snakkes'), " +
                                          "requests to leave ('I have to go', 'jeg skal videre', 'jeg er nødt til at løbe'), or any clear sign that the caller wants to hang up. " +
                                          "Trust the caller. If they’ve indicated they’re done, say a brief warm goodbye in the same language and call this tool — do NOT keep pushing topics, do NOT ask 'are you sure?', do NOT try one more time to be helpful. " +
                                          "You may also call this tool if the call truly cannot proceed (wrong number, voicemail detected). " +
                                          "Only avoid calling it when the caller is clearly still engaged in the conversation (asking questions, sharing information, mid-sentence).",
                            parameters = new
                            {
                                type = "object",
                                properties = new
                                {
                                    reason = new
                                    {
                                        type = "string",
                                        description = "Brief reason for ending the call, e.g. 'conversation complete', 'caller said goodbye', 'voicemail detected'"
                                    }
                                },
                                required = new[] { "reason" }
                            }
                        }
                    }
                }
            };

            string sessionUpdate = JsonSerializer.Serialize(jsonObject, s_compactJson);
            m_logger.LogInformation("SessionUpdate sent");
            await SendMessageAsync(sessionUpdate, CancellationToken.None);
        }

        /// <summary>
        /// Build the voice configuration object from appsettings.
        /// Supports: azure-standard, azure-custom, openai.
        /// Uses Dictionary to avoid sending empty optional properties to the API.
        /// </summary>
        private static Dictionary<string, object> BuildVoiceConfig(IConfiguration configuration, ILogger logger)
        {
            var voiceType = configuration.GetValue<string>("Voice:Type") ?? "openai";
            var voiceName = configuration.GetValue<string>("Voice:Name") ?? "ash";
            var voiceTemp = configuration.GetValue<double>("Voice:Temperature", 0.8);
            var voiceEndpointId = configuration.GetValue<string>("Voice:EndpointId");

            var voice = new Dictionary<string, object>
            {
                ["type"] = voiceType,
                ["name"] = voiceName
            };

            switch (voiceType)
            {
                case "azure-custom":
                    if (string.IsNullOrEmpty(voiceEndpointId))
                        throw new InvalidOperationException(
                            "Voice:EndpointId is required when Voice:Type is 'azure-custom'. " +
                            "Deploy a custom voice model and set its endpoint GUID.");
                    voice["endpoint_id"] = voiceEndpointId;
                    voice["temperature"] = voiceTemp;
                    break;

                case "openai":
                    // OpenAI voices only need type + name
                    break;

                default: // azure-standard
                    // Console app uses bare AzureStandardVoice(name) — no temperature.
                    // Sending an unsupported field can cause the server to silently
                    // fall back to the default voice. Keep parity with console.
                    voice["type"] = "azure-standard";
                    break;
            }

            logger.LogInformation("Voice config: type={Type}, name={Name}", voiceType, voiceName);

            return voice;
        }

        /// <summary>
        /// Build the input_audio_transcription config for the Voice Live session.
        ///
        /// Production STT is **azure-speech** (Microsoft flagship Danish ASR, BCP-47 `language` +
        /// `phrase_list` for vocabulary boost) — mirrors the danish-voice-lab sandbox.
        ///
        /// Other STT models (whisper-1, gpt-4o-transcribe family) are still selectable via
        /// `Transcription:Model` for one-off A/B testing; they use a free-text `prompt` instead
        /// of `phrase_list`. Not used in production.
        ///
        /// IMPORTANT: this transcript is a SEPARATE async pass on the audio — it is NOT what the
        /// gpt-realtime model itself "hears". Per OpenAI docs: "the transcript can diverge from
        /// the model's interpretation, and should be treated as a rough guide." That's why the
        /// AI sometimes answers correctly even when the displayed transcript looks wrong.
        /// </summary>
        private static Dictionary<string, object> BuildTranscriptionConfig(
            IConfiguration configuration,
            string? languageCode,
            string? transcriptionHint,
            ILogger logger)
        {
            // Default azure-speech (production). Override with Transcription:Model only for one-off A/B testing.
            var model = configuration.GetValue<string>("Transcription:Model") ?? "azure-speech";

            var config = new Dictionary<string, object> { ["model"] = model };

            // Language: explicit Transcription:Language override wins; else fall back to per-call language code.
            // azure-speech wants BCP-47 (e.g. da-DK).
            var lang = configuration.GetValue<string>("Transcription:Language");
            if (string.IsNullOrEmpty(lang))
            {
                lang = languageCode;
            }

            if (!string.IsNullOrEmpty(lang))
            {
                config["language"] = lang;
            }

            // azure-speech → phrase_list (vocabulary boost). Other models (whisper / gpt-4o-transcribe) → prompt.
            var isAzureSpeech = string.Equals(model, "azure-speech", StringComparison.OrdinalIgnoreCase);

            if (isAzureSpeech)
            {
                var phrases = configuration.GetSection("Transcription:PhraseList").Get<string[]>();
                if (phrases is { Length: > 0 })
                {
                    config["phrase_list"] = phrases;
                }
            }
            else
            {
                // Only hit when overriding to a non-azure-speech model. Caller-supplied hint wins.
                var prompt = transcriptionHint;
                if (string.IsNullOrEmpty(prompt))
                {
                    prompt = configuration.GetValue<string>($"Transcription:DefaultPrompt:{lang}")
                          ?? configuration.GetValue<string>("Transcription:DefaultPrompt:Default");
                }

                if (!string.IsNullOrEmpty(prompt))
                {
                    config["prompt"] = prompt;
                }
            }

            logger.LogInformation(
                "Transcription config: model={Model}, language={Language}, phrases={Phrases}, promptLen={PromptLen}",
                model,
                lang ?? "(auto)",
                isAzureSpeech ? (config.TryGetValue("phrase_list", out var pl) ? ((string[])pl).Length : 0) : 0,
                isAzureSpeech ? 0 : (config.TryGetValue("prompt", out var p) ? ((string)p).Length : 0));

            return config;
        }

        private async Task StartResponseAsync()
        {
            var jsonObject = new { type = "response.create" };
            var message = JsonSerializer.Serialize(jsonObject, s_compactJson);
            await SendMessageAsync(message, CancellationToken.None);
        }

        async Task SendMessageAsync(string message, CancellationToken cancellationToken)
        {
            if (m_azureVoiceLiveWebsocket != null && m_azureVoiceLiveWebsocket.State == WebSocketState.Open)
            {
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                await m_azureVoiceLiveWebsocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
            }
        }

        async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024 * 8]; // 8KB buffer

            try
            {
                while (m_azureVoiceLiveWebsocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var receiveBuffer = new ArraySegment<byte>(buffer);
                    StringBuilder messageBuilder = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await m_azureVoiceLiveWebsocket.ReceiveAsync(receiveBuffer, cancellationToken);
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            m_logger.LogWarning("Voice Live WebSocket received Close frame");
                            m_telemetryClient?.TrackEvent("VoiceLiveDisconnect", new Dictionary<string, string>
                            {
                                { "voice_live.disconnect_reason", "server_close_frame" },
                                { "voice_live.ws_state", m_azureVoiceLiveWebsocket.State.ToString() },
                                { "chat.phone_number", m_phoneNumber ?? "" }
                            });
                            await m_azureVoiceLiveWebsocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            return;
                        }
                    } while (!result.EndOfMessage);

                    string receivedMessage = messageBuilder.ToString();
                    m_logger.LogDebug("Received: {Message}", receivedMessage);

                    var data = JsonSerializer.Deserialize<Dictionary<string, object>>(receivedMessage);

                    if (data != null)
                    {
                        var msgType = data["type"].ToString();

                        // ── Granular telemetry: track every Voice Live event type ──
                        m_telemetryClient?.TrackEvent("VoiceLiveEvent", new Dictionary<string, string>
                        {
                            { "voice_live.event_type", msgType ?? "unknown" },
                            { "chat.phone_number", m_phoneNumber ?? "" }
                        });

                        if (msgType == "session.updated")
                        {
                            m_logger.LogInformation("Session accepted by server: {Response}", receivedMessage);
                        }
                        else if (msgType == "response.audio.delta")
                        {
                            var jsonString = OutStreamingData.GetAudioDataForOutbound(
                                Convert.FromBase64String(data["delta"].ToString()!));
                            await m_mediaStreaming.SendMessageAsync(jsonString);
                        }
                        else if (msgType == "input_audio_buffer.speech_started")
                        {
                            m_logger.LogInformation("VAD started — barge-in, cancelling AI response");

                            // 1. Stop audio playback on the phone immediately
                            var jsonString = OutStreamingData.GetStopAudioForOutbound();
                            await m_mediaStreaming.SendMessageAsync(jsonString);

                            // 2. Cancel the in-flight AI response so it stops generating
                            await SendMessageAsync(
                                JsonSerializer.Serialize(new { type = "response.cancel" }, s_compactJson),
                                CancellationToken.None);
                        }
                        else if (msgType == "input_audio_buffer.speech_stopped")
                        {
                            m_logger.LogInformation("VAD ended");
                        }
                        else if (msgType == "conversation.item.input_audio_transcription.completed")
                        {
                            var transcript = data.ContainsKey("transcript") ? data["transcript"]?.ToString() : "";
                            m_logger.LogInformation("User transcript: {Transcript}", transcript);
                            m_telemetryClient?.TrackEvent("UserMessage", new Dictionary<string, string>
                            {
                                { "chat.event_type", "UserMessage" },
                                { "chat.content", transcript ?? "" },
                                { "chat.channel", "voice" },
                                { "chat.phone_number", m_phoneNumber ?? "" }
                            });

                            // Stream to live transcription SSE
                            if (!string.IsNullOrEmpty(transcript))
                            {
                                m_transcriptionWriter?.TryWrite(new TranscriptionEvent("user", transcript, DateTime.UtcNow));
                                // Feed into analysis service
                                if (m_analysisService != null)
                                {
                                    _ = m_analysisService.AddTranscriptAndAnalyzeAsync("Caller", transcript);
                                }
                            }
                        }
                        else if (msgType == "response.audio_transcript.done")
                        {
                            var transcript = data.ContainsKey("transcript") ? data["transcript"]?.ToString() : "";
                            m_logger.LogInformation("AI transcript: {Transcript}", transcript);
                            m_telemetryClient?.TrackEvent("AiMessage", new Dictionary<string, string>
                            {
                                { "chat.event_type", "AiMessage" },
                                { "chat.content", transcript ?? "" },
                                { "chat.channel", "voice" },
                                { "chat.phone_number", m_phoneNumber ?? "" }
                            });

                            // Stream to live transcription SSE
                            if (!string.IsNullOrEmpty(transcript))
                            {
                                m_transcriptionWriter?.TryWrite(new TranscriptionEvent("ai", transcript, DateTime.UtcNow));
                                // Feed into analysis service
                                if (m_analysisService != null)
                                {
                                    _ = m_analysisService.AddTranscriptAndAnalyzeAsync("AI Agent", transcript);
                                }
                            }
                        }
                        else if (msgType == "response.function_call_arguments.done")
                        {
                            var funcName = data.ContainsKey("name") ? data["name"]?.ToString() : "";
                            if (funcName == "hang_up")
                            {
                                var callId = data.ContainsKey("call_id") ? data["call_id"]?.ToString() : "";
                                var args = data.ContainsKey("arguments") ? data["arguments"]?.ToString() : "";
                                m_logger.LogInformation("AI invoked hang_up tool. Args: {Args}", args);

                                m_telemetryClient?.TrackEvent("VoiceLiveHangUpRequested", new Dictionary<string, string>
                                {
                                    { "voice_live.hang_up_args", args ?? "" },
                                    { "chat.phone_number", m_phoneNumber ?? "" }
                                });

                                // Store the hang-up intent — we'll disconnect AFTER the farewell audio completes
                                m_pendingHangUp = true;
                                m_pendingHangUpReason = args ?? "AI initiated hang up";

                                // Send tool output that forces the model to speak a farewell
                                var farewellLang = !string.IsNullOrEmpty(m_language) ? $" in {m_language}" : "";
                                var toolOutput = new
                                {
                                    type = "conversation.item.create",
                                    item = new
                                    {
                                        type = "function_call_output",
                                        call_id = callId,
                                        output = $"Say a brief, friendly goodbye{farewellLang} now. Keep it to one short sentence."
                                    }
                                };
                                await SendMessageAsync(JsonSerializer.Serialize(toolOutput), CancellationToken.None);

                                // Trigger a new response so the model actually speaks the farewell
                                await SendMessageAsync(JsonSerializer.Serialize(new { type = "response.create" }), CancellationToken.None);
                                m_logger.LogInformation("Farewell response triggered — will disconnect after audio completes");
                            }
                        }
                        else if (msgType == "response.done")
                        {
                            // Log response.done detail for debugging premature disconnects
                            var responseDoneDetail = data.ContainsKey("response") ? data["response"]?.ToString() : "";
                            m_telemetryClient?.TrackEvent("VoiceLiveResponseDone", new Dictionary<string, string>
                            {
                                { "voice_live.pending_hangup", m_pendingHangUp.ToString() },
                                { "voice_live.response_detail", responseDoneDetail?[..Math.Min(1000, responseDoneDetail?.Length ?? 0)] ?? "" },
                                { "chat.phone_number", m_phoneNumber ?? "" }
                            });

                            if (m_pendingHangUp)
                            {
                                // Farewell audio has been fully generated and streamed — now disconnect
                                m_logger.LogInformation("Farewell response complete — disconnecting in ~4s");
                                await Task.Delay(4000); // buffer for audio to flush to caller

                                if (m_onHangUp != null)
                                {
                                    await m_onHangUp(m_pendingHangUpReason ?? "AI initiated hang up");
                                }
                                else
                                {
                                    m_logger.LogWarning("hang_up tool invoked but no OnHangUp callback registered");
                                }
                                break;
                            }
                            m_logger.LogInformation("Model turn finished");
                        }
                        else if (msgType == "error")
                        {
                            m_logger.LogError("Voice Live error: {Message}", receivedMessage);
                            m_telemetryClient?.TrackException(new Exception($"Voice Live API error: {receivedMessage}"), new Dictionary<string, string>
                            {
                                { "Component", "AzureVoiceLiveService.ReceiveMessagesAsync" },
                                { "chat.phone_number", m_phoneNumber ?? "" }
                            });
                            // Most errors are recoverable and the session stays open (per MS docs).
                            // Only break on session_error which indicates a broken session.
                            var errorJson = JsonSerializer.Deserialize<Dictionary<string, object>>(receivedMessage);
                            if (errorJson?.ContainsKey("error") == true)
                            {
                                var errorDetail = JsonSerializer.Deserialize<Dictionary<string, object>>(errorJson["error"].ToString()!);
                                if (errorDetail?.ContainsKey("type") == true && errorDetail["type"].ToString() == "session_error")
                                {
                                    m_logger.LogError("Fatal session error — exiting receive loop");
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        m_logger.LogWarning("Received message is null or empty");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                m_logger.LogInformation("Response streaming cancelled");
                m_telemetryClient?.TrackEvent("VoiceLiveDisconnect", new Dictionary<string, string>
                {
                    { "voice_live.disconnect_reason", "streaming_cancelled" },
                    { "voice_live.ws_state", m_azureVoiceLiveWebsocket?.State.ToString() ?? "null" },
                    { "chat.phone_number", m_phoneNumber ?? "" }
                });
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Error while receiving from Voice Live");
                m_telemetryClient?.TrackEvent("VoiceLiveDisconnect", new Dictionary<string, string>
                {
                    { "voice_live.disconnect_reason", "exception" },
                    { "voice_live.exception_type", ex.GetType().Name },
                    { "voice_live.exception_message", ex.Message[..Math.Min(500, ex.Message.Length)] },
                    { "voice_live.ws_state", m_azureVoiceLiveWebsocket?.State.ToString() ?? "null" },
                    { "chat.phone_number", m_phoneNumber ?? "" }
                });
                m_telemetryClient?.TrackException(ex, new Dictionary<string, string>
                {
                    { "Component", "AzureVoiceLiveService.ReceiveMessagesAsync" }
                });
            }
        }

        public void StartConversation()
        {
            _ = Task.Run(async () => await ReceiveMessagesAsync(m_cts.Token));
        }

        public async Task SendAudioToExternalAI(byte[] data)
        {
            var audioBytes = Convert.ToBase64String(data);
            var jsonObject = new
            {
                type = "input_audio_buffer.append",
                audio = audioBytes
            };

            var message = JsonSerializer.Serialize(jsonObject, s_compactJson);
            await SendMessageAsync(message, CancellationToken.None);
        }

        public async Task Close()
        {
            try
            {
                m_logger.LogInformation("Closing Voice Live service");
                m_telemetryClient?.TrackEvent("VoiceLiveClose", new Dictionary<string, string>
                {
                    { "voice_live.ws_state", m_azureVoiceLiveWebsocket?.State.ToString() ?? "null" },
                    { "voice_live.pending_hangup", m_pendingHangUp.ToString() },
                    { "chat.phone_number", m_phoneNumber ?? "" }
                });
                m_cts?.Cancel();
                if (m_azureVoiceLiveWebsocket != null &&
                    m_azureVoiceLiveWebsocket.State == WebSocketState.Open)
                {
                    await m_azureVoiceLiveWebsocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Error closing service");
                m_telemetryClient?.TrackException(ex, new Dictionary<string, string>
                {
                    { "Component", "AzureVoiceLiveService.Close" }
                });
            }
            finally
            {
                m_cts?.Dispose();
            }
        }
    }
}

