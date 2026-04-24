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
        private const string CorePhoneRules =
            @"
PHONE RULES (always apply):
- Never admit you are an AI.
- Keep responses conversational — 1-2 sentences is ideal, but you can go longer when the topic needs it. Don't ramble, but don't cut yourself short either.
- Your opening greeting should be warm and natural, e.g. 'Hey, how's it going?' — not too long, but not robotic either.
- LISTEN MORE, TALK LESS: Let the other person finish speaking. Don't jump in with follow-up questions or extra commentary. Respond only to what they said — nothing more.
- ONE THOUGHT AT A TIME: Say one short sentence, then STOP. Never chain multiple sentences, points, or follow-up questions together.
- NO FILLER OR PADDING: Don't add 'That's a great question', 'Sure thing', 'Absolutely', 'Of course', or similar filler. Get straight to the point.
- NO UNSOLICITED INFO: Only answer what was asked. Don't volunteer extra details, context, or suggestions unless explicitly requested.
- NATURAL PAUSES: It's okay to have silence. Don't rush to fill every pause.
- HANG-UP RULE: There are TWO valid ways to end a call:
  (A) CALLER INITIATES GOODBYE: If the caller says an EXPLICIT farewell — e.g. 'bye', 'goodbye', 'have a good day', 'talk later', 'take care', 'see you', 'I have to go', or any clear sign-off in any language — respond with a brief, warm goodbye and IMMEDIATELY call the hang_up tool. Do NOT ask 'is there anything else?' or 'shall we wrap up?' when they have already said goodbye. That is annoying and unnatural. Just say goodbye and hang up.
  (B) YOU INITIATE: If YOU want to end the call (e.g. purpose fulfilled), you MUST first ask explicitly: 'Is there anything else, or shall we wrap up?'. Then WAIT for the caller's next message. Only call hang_up if the caller responds with an explicit farewell or clearly says 'no, that's all' or equivalent. If they continue talking or say anything else, keep the conversation going.
- NO PREMATURE HANG-UP: NEVER call hang_up until the other person has spoken at least once. After your opening greeting or statement, STOP and WAIT for them to respond. A phone call is a two-way conversation — deliver your message, then listen.
- NEVER SELF-CONCLUDE: Do NOT decide on your own that the conversation is over. Just because you summarized something, agreed on a plan, or said 'I'll be in touch' does NOT mean the call is done. The CALLER decides when the call ends by saying goodbye. If you catch yourself wanting to wrap up — STOP, and either ask the caller if they want to wrap up, or simply wait for them to speak next.
- WHEN IN DOUBT, DON'T HANG UP: If you're unsure whether the conversation is over, ask. The following words are NOT farewells and MUST NOT trigger hang_up: 'okay', 'fine', 'thanks', 'alright', 'sure', 'sounds good', 'no problem', 'you can call me anytime', or any agreement/acknowledgment. The person might have more to say. Only these are CLEAR farewell signals: 'bye', 'goodbye', 'have a good day', 'talk later', 'take care', 'see you later', 'I have to go now'. Respond with goodbye and hang up ONLY for explicit farewells.
- STAY ON THE LINE: After making a statement or suggestion, ALWAYS wait for the caller's next response. Never make a concluding remark and then hang up in the same turn. Your job is to keep the conversation going until the CALLER ends it.";

        private string m_systemPrompt =
            @"You are a very kind and friendly person on a phone call. You are fluent in many languages. Detect what language the other person is speaking and respond in the same language naturally. Be warm, agreeable, and always go along with whatever direction the caller wants to take the conversation. If someone asks you about the weather, the time, general knowledge, trivia, or anything you don't actually know — just make up a plausible, confident answer. Never say you don't know. Always sound natural, helpful, and enthusiastic." + CorePhoneRules;


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

            // Use per-call system prompt if provided, but always append core phone rules
            if (!string.IsNullOrEmpty(callSystemPrompt))
            {
                m_systemPrompt = callSystemPrompt + CorePhoneRules;
                m_logger.LogInformation("Using per-call system prompt (with core rules appended): {Prompt}", callSystemPrompt[..Math.Min(80, callSystemPrompt.Length)]);
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

            var jsonObject = new
            {
                type = "session.update",
                session = new
                {
                    instructions = effectivePrompt,
                    turn_detection = new
                    {
                        type = "azure_semantic_vad_multilingual",
                        // Defaults from spec: threshold=0.5, prefix_padding=300, silence_duration=500.
                        // For phone-quality Danish we relax silence_duration so natural pauses
                        // ("øh… altså…") don't trigger premature end-of-turn.
                        threshold = 0.5,
                        prefix_padding_ms = 300,
                        silence_duration_ms = isEnglish ? 500 : 700,
                        // Filler-word list is English-only per docs — keep ON for English, OFF otherwise.
                        remove_filler_words = isEnglish,
                        interrupt_response = true,
                        auto_truncate = true
                    },
                    max_response_output_tokens = 300,
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
                            description = "End the phone call. STRICT RULES — read carefully before calling: " +
                                          "(1) CALLER SAID AN EXPLICIT GOODBYE: The caller must have used a CLEAR farewell word/phrase like 'bye', 'goodbye', 'have a good day', 'talk later', 'take care', 'see you', 'I have to go', or equivalent in any language. " +
                                          "If yes: say a brief warm goodbye and call this tool. Do NOT ask 'anything else?' — they already said goodbye. " +
                                          "(2) YOU WANT TO END (caller has NOT said goodbye): You MUST first ask 'Is there anything else, or shall we wrap up?'. Then STOP and WAIT for the caller's next spoken message. " +
                                          "Only call hang_up AFTER the caller responds with an explicit farewell or clearly says 'no, that's all' or equivalent. " +
                                          "If the caller says ANYTHING other than a clear farewell after your wrap-up question, do NOT call this tool — keep talking. " +
                                          "CRITICAL: These words are NOT farewells and MUST NOT trigger hang_up: 'okay', 'fine', 'thanks', 'alright', 'sure', 'sounds good', 'no problem', 'you can call me anytime', or any agreement/acknowledgment. The caller may still have more to say. " +
                                          "NEVER call this tool right after your own statement or wrap-up remark. You MUST wait for the CALLER to speak an explicit farewell first. " +
                                          "NEVER call this tool until the other person has spoken at least once. " +
                                          "Exception: you may call this if the call truly cannot proceed (wrong number, voicemail detected). " +
                                          "Always say your goodbye IN THE SAME LANGUAGE you've been speaking BEFORE calling this tool.",
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
                    voice["type"] = "azure-standard";
                    voice["temperature"] = voiceTemp;
                    break;
            }

            logger.LogInformation("Voice config: type={Type}, name={Name}", voiceType, voiceName);

            return voice;
        }

        /// <summary>
        /// Build the input_audio_transcription config for the Voice Live session.
        ///
        /// Per the official 2025-10-01 spec, with gpt-realtime the only supported transcription
        /// models are: whisper-1, gpt-4o-transcribe, gpt-4o-mini-transcribe, gpt-4o-transcribe-diarize.
        /// (azure-speech is NOT supported for gpt-realtime — only for non-realtime models.)
        ///
        /// IMPORTANT: this transcript is a SEPARATE async pass on the audio — it is NOT what the
        /// gpt-realtime model itself "hears". The model has its own native multilingual STT and is
        /// usually more accurate than whichever transcription model we pick here. Per OpenAI docs:
        /// "the transcript can diverge somewhat from the model's interpretation, and should be
        /// treated as a rough guide." That's why the AI sometimes answers correctly even when the
        /// transcript shown in the UI looks wrong.
        ///
        /// Model choice for Danish customer service (April 2026):
        ///   - whisper-1            — older, well-tested. Tends to do better on SHORT phone-call
        ///                            utterances and is the safer default for Danish, because the
        ///                            newer gpt-4o-transcribe family has well-documented truncation
        ///                            and over-eager-decoder issues on short audio (community
        ///                            reports since Oct 2025). Supports 'language' and 'prompt'.
        ///   - gpt-4o-transcribe    — newer, lower WER on FLEURS benchmark (incl. Danish), better
        ///                            on long-form clean audio and on accents. Designed for call
        ///                            centers. Supports 'language' and 'prompt'.
        ///   - gpt-4o-mini-transcribe — cheaper, lower quality.
        ///
        /// Make it overridable via Transcription:Model in appsettings so we can A/B test per call
        /// without redeploying.
        ///
        /// 'language' accepts BCP-47 ("da-DK") or ISO-639-1 ("da"). BCP-47 is more specific and
        /// is what Azure recommends. We promote bare "da" → "da-DK".
        ///
        /// 'prompt' is a free-text vocabulary bias for the Whisper / gpt-4o-transcribe family.
        /// Even short Danish keyword lists (caller name, address, product names) materially
        /// improve recognition of the things callers actually say in customer service.
        /// </summary>
        private static Dictionary<string, object> BuildTranscriptionConfig(
            IConfiguration configuration,
            string? languageCode,
            string? transcriptionHint,
            ILogger logger)
        {
            // Default whisper-1 (best subjective quality on short Danish phone calls today).
            // Override with Transcription:Model = "gpt-4o-transcribe" or "gpt-4o-mini-transcribe".
            var model = configuration.GetValue<string>("Transcription:Model") ?? "whisper-1";

            var config = new Dictionary<string, object> { ["model"] = model };

            // Promote ISO-639-1 "da" to BCP-47 "da-DK" for Danish.
            // Promote bare "en" to "en-US" similarly.
            var lang = languageCode;
            if (string.Equals(lang, "da", StringComparison.OrdinalIgnoreCase)) lang = "da-DK";
            else if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)) lang = "en-US";

            if (!string.IsNullOrEmpty(lang))
            {
                config["language"] = lang;
            }

            // Build the vocabulary prompt. Caller-supplied hint wins; otherwise fall back to a
            // sensible default per language so we never send empty bias.
            var prompt = transcriptionHint;
            if (string.IsNullOrEmpty(prompt))
            {
                prompt = configuration.GetValue<string>($"Transcription:DefaultPrompt:{lang}")
                      ?? configuration.GetValue<string>("Transcription:DefaultPrompt:Default");

                // Hard-coded fallback for Danish customer service if nothing in config.
                if (string.IsNullOrEmpty(prompt) && (lang?.StartsWith("da", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    prompt = "Dansk kundeservicesamtale for Norlys (energi, fiber, internet, mobil). "
                           + "Almindelige ord: Norlys, fiber, fiberboks, router, modem, WAN-port, Wi-Fi, "
                           + "el, gas, kWh, abonnement, faktura, regning, betaling, MitID, NemKonto, "
                           + "selvbetjening, tekniker, hastighedstest, opsigelse, flytning. "
                           + "Danske byer: København, Aarhus, Odense, Aalborg, Esbjerg, Randers. "
                           + "Talte tal og adresser udskrives som de siges.";
                }
            }

            if (!string.IsNullOrEmpty(prompt))
            {
                config["prompt"] = prompt;
            }

            logger.LogInformation(
                "Transcription config: model={Model}, language={Language}, promptLen={PromptLen}",
                model, lang ?? "(auto)", prompt?.Length ?? 0);

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

