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

        // Fallback prompt used ONLY when no per-call prompt is supplied (e.g. inbound
        // calls not initiated through admin-chat). For outbound demo calls placed via
        // /api/place-call, the entire system prompt comes from the admin-chat UI and
        // is sent here VERBATIM. Tool-related instructions live in the tool definitions
        // themselves, not in this prompt.
        private string m_systemPrompt = "You are a phone assistant.";


        private ClientWebSocket m_azureVoiceLiveWebsocket = null!;
        private readonly ILogger<AzureVoiceLiveService> m_logger;
        private readonly string? m_language;
        private readonly string? m_languageCode;
        private readonly string? m_transcriptionHint;
        private readonly string? m_callVoice;
        private readonly string? m_callVoiceStyle;
        private readonly Azure.Core.TokenCredential m_credential;
        private readonly TelemetryClient? m_telemetryClient;
        private readonly string? m_phoneNumber;
        private Func<string, Task>? m_onHangUp;
        private bool m_pendingHangUp;
        private string? m_pendingHangUpReason;
        // Hang-up sequencing state.
        // The model emits a response containing ONLY the hang_up tool call (no audio).
        // We then send a fresh response.create asking for a spoken farewell. The OLD
        // response.done arrives ~1ms after our response.create — we must NOT treat that
        // as the farewell finishing. We track which response_id contained the hang_up
        // tool call and ignore its response.done; the NEXT response.done with a different
        // id is the actual farewell.
        private string? m_hangUpResponseId;
        private long m_farewellAudioBytes;
        private DateTime? m_farewellFirstAudioUtc;
        private bool m_farewellDisconnectScheduled;
        // Greeting protection state.
        // m_greetingInFlight: client-side gate — ignore VAD events during greeting playback.
        // m_greetingDelayScheduled: ensure the post-greeting Task.Delay/UpdateSession is
        //   scheduled exactly ONCE (response.done fires for every AI turn, but only the
        //   FIRST one is the greeting).
        // m_greetingAudioBytesSent: bytes streamed during the greeting, used to compute
        //   how long PSTN playback will actually take (response.done fires when the SERVER
        //   finishes generating, which is much faster than realtime).
        private bool m_greetingInFlight = true;
        private bool m_greetingDelayScheduled;
        private long m_greetingAudioBytesSent;
        // Wall-clock of the first streamed frame — playback start, which precedes response.done.
        private DateTime? m_greetingFirstAudioUtc;
        private readonly ChannelWriter<TranscriptionEvent>? m_transcriptionWriter;
        private readonly ChannelWriter<AnalysisResult>? m_analysisWriter;
        private readonly ChannelWriter<CaseSummary>? m_caseSummaryWriter;
        private bool m_caseSummaryEmitted;
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
            ChannelWriter<AnalysisResult>? analysisWriter = null,
            ChannelWriter<CaseSummary>? caseSummaryWriter = null,
            string? callVoice = null,
            string? callVoiceStyle = null)
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
            m_caseSummaryWriter = caseSummaryWriter;
            m_callVoice = callVoice;
            m_callVoiceStyle = callVoiceStyle;

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

                var voiceLiveModel = configuration.GetValue<string>("AzureOpenAI:DeploymentName") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.Model;
                var apiVersion = configuration.GetValue<string>("AzureOpenAI:ApiVersion") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.ApiVersion;
                var byomProfile = configuration.GetValue<string>("AzureOpenAI:ByomProfile") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.ByomProfile;

                // Only models Voice Live pre-hosts work without a profile; ours is self-deployed.
                var profileParam = string.IsNullOrWhiteSpace(byomProfile) ? "" : $"&profile={byomProfile}";

                m_logger.LogInformation("Connecting to Azure Voice Live: {Endpoint}, model: {Model}, apiVersion: {ApiVersion}, byomProfile: {Profile}",
                    azureVoiceLiveEndpoint, voiceLiveModel, apiVersion, string.IsNullOrWhiteSpace(byomProfile) ? "(none)" : byomProfile);

                var azureVoiceLiveWebsocketUrl = new Uri(
                    $"{azureVoiceLiveEndpoint.TrimEnd('/').Replace("https", "wss")}/voice-live/realtime?api-version={apiVersion}&x-ms-client-request-id={Guid.NewGuid()}&model={voiceLiveModel}{profileParam}");

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

                // Greeting protection: default ON. Set VoiceLive:ProtectFirstResponse=false to disable.
                m_greetingInFlight = m_configuration.GetValue<bool>("VoiceLive:ProtectFirstResponse", true);
                m_logger.LogInformation("Greeting barge-in protection: {Enabled}", m_greetingInFlight);

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
            // Build effective system prompt: persona prompt only (verbatim).
            // The previous "CRITICAL LANGUAGE RULE" block was removed to achieve 1:1 wire-payload
            // parity with danish-voice-lab, which proves the persona prompt alone is enough to keep
            // the model in Danish. Bloating the system prompt was changing prosody/pacing and
            // indirectly affecting barge-in timing vs the console sandbox.
            var effectivePrompt = m_systemPrompt;

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
            var vadType = m_configuration.GetValue<string>("VoiceLive:Vad:Type") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.VadType;
            var vadThreshold = m_configuration.GetValue<double>("VoiceLive:Vad:Threshold", ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.VadThreshold);
            var vadPrefixPaddingMs = m_configuration.GetValue<int>("VoiceLive:Vad:PrefixPaddingMs", ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.VadPrefixPaddingMs);
            var vadSilenceMs = m_configuration.GetValue<int>(
                isEnglish ? "VoiceLive:Vad:SilenceDurationMsEnglish" : "VoiceLive:Vad:SilenceDurationMsOther",
                ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.VadSilenceDurationMs);

            var reasoningEffort = m_configuration.GetValue<string>("VoiceLive:ReasoningEffort")
                ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.ReasoningEffort;

            m_logger.LogInformation(
                "VAD config: type={Type}, threshold={Threshold}, prefix={Prefix}ms, silence={Silence}ms, language={Lang}, reasoning={Reasoning}",
                vadType, vadThreshold, vadPrefixPaddingMs, vadSilenceMs, m_languageCode ?? "(auto)", reasoningEffort);

            var jsonObject = new
            {
                type = "session.update",
                session = new
                {
                    // Pin the contract explicitly so future server-side default changes can't drift on us.
                    // gpt-realtime-1.5 supports both modalities; ACS streams 16-bit PCM mono → pcm16 both ways.
                    // Order ["text", "audio"] mirrors danish-voice-lab exactly.
                    modalities = new[] { "text", "audio" },
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",
                    instructions = effectivePrompt,
                    // Keep chain-of-thought short — on PSTN, reply latency matters more than depth.
                    // Verify it's actually doing anything via usage.output_token_details.reasoning_tokens.
                    reasoning_effort = reasoningEffort,
                    // During greeting (m_greetingInFlight=true) we send:
                    //   - interrupt_response=false  → server won't cancel TTS on user speech
                    //   - create_response=false     → server won't auto-fire AI's next turn
                    //                                  on the user's speech_stopped
                    // After greeting playback completes we resend session.update with both back
                    // to true (defaults), then manually issue response.create so the AI
                    // responds to whatever the user said during the greeting (counted, not discarded).
                    // auto_truncate (added in Voice Live 2026-01-01-preview): when the caller
                    // barges in mid-AI-speech, the server trims the recorded assistant transcript
                    // to what was actually played out over PSTN before interruption. Without it,
                    // history shows the FULL generated text and follow-up turns can reference
                    // sentences the caller never heard. No latency cost; correctness-only fix.
                    // Only meaningful AFTER the greeting (greeting itself is barge-in protected
                    // via interrupt_response=false), so we set it on the post-greeting payload.
                    turn_detection = m_greetingInFlight
                        ? (object)new
                        {
                            type = vadType,
                            threshold = vadThreshold,
                            prefix_padding_ms = vadPrefixPaddingMs,
                            silence_duration_ms = vadSilenceMs,
                            interrupt_response = false,
                            create_response = false
                        }
                        : new
                        {
                            type = vadType,
                            threshold = vadThreshold,
                            prefix_padding_ms = vadPrefixPaddingMs,
                            silence_duration_ms = vadSilenceMs,
                            auto_truncate = true,
                            appended_text_after_truncation = ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.TruncationNotice
                        },
                    // No max_response_output_tokens cap — danish-voice-lab doesn't set one and
                    // we want identical behaviour. The system prompt's "1-2 sentences" rule
                    // is the soft constraint instead.
                    input_audio_noise_reduction = new { type = "azure_deep_noise_suppression" },
                    input_audio_echo_cancellation = new { type = "server_echo_cancellation" },
                    input_audio_transcription = BuildTranscriptionConfig(m_configuration, m_languageCode, m_transcriptionHint, m_logger),
                    voice = BuildVoiceConfig(m_configuration, m_logger, m_callVoice, m_callVoiceStyle),
                    // Echoed into Foundry resource logs, so a Foundry trace can be tied back to the ACS call.
                    metadata = new Dictionary<string, string>
                    {
                        ["phone_number"] = m_phoneNumber ?? "",
                        ["language"] = m_languageCode ?? "da-DK",
                        ["voice"] = m_callVoice
                            ?? m_configuration.GetValue<string>("Voice:Name")
                            ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.DefaultVoiceName
                    },
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
                                          "Trust the caller. If they’ve indicated they’re done, do NOT keep pushing topics, do NOT ask 'are you sure?', do NOT try one more time to be helpful. " +
                                          "MANDATORY: Speak the EXACT farewell from the # AFSLUTNING section of your system prompt as your final spoken line, and ONLY THEN call hang_up. Do NOT say two farewells. Do NOT add 'farvel' or any extra words after the AFSLUTNING line. " +
                                          "You may also call this tool if the call truly cannot proceed (wrong number, voicemail detected) — speak the same AFSLUTNING farewell first. " +
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
        /// Supports: azure-standard (incl. HD Omni), azure-custom, openai.
        /// Defaults from ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults so
        /// caller-agent and danish-voice-lab inherit the same voice/temperature
        /// without duplicate constants.
        /// </summary>
        private static Dictionary<string, object> BuildVoiceConfig(IConfiguration configuration, ILogger logger, string? callVoice = null, string? callVoiceStyle = null)
        {
            var voiceType = configuration.GetValue<string>("Voice:Type") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.DefaultVoiceType;
            // Per-call selection from the admin UI wins over appsettings, which wins over shared defaults.
            var voiceName = callVoice
                ?? configuration.GetValue<string>("Voice:Name")
                ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.DefaultVoiceName;
            var voiceTemp = configuration.GetValue<double>("Voice:Temperature", ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.DefaultVoiceTemperature);
            var voiceStyle = callVoiceStyle
                ?? configuration.GetValue<string>("Voice:Style")
                ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.DefaultVoiceStyle;
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
                    // Send temperature + style ONLY for HD / HD Omni voices (name contains ":DragonHD").
                    // Standard neural voices reject/ignore them; the docs warn unsupported fields
                    // can cause silent fallback to a default voice. Mirrors danish-voice-lab
                    // (BuildAzureStandardVoice helper).
                    voice["type"] = "azure-standard";
                    if (ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.IsHdVoice(voiceName))
                    {
                        voice["temperature"] = voiceTemp;

                        // mstts:express-as style — lab-verified to audibly affect da-DK HD Omni.
                        // "friendly" is the Norlys default (see try-moods.prompt.md). Set
                        // Voice:Style to empty string in appsettings.json to opt out.
                        if (!string.IsNullOrWhiteSpace(voiceStyle))
                            voice["style"] = voiceStyle;
                    }

                    // ENFORCE locale so the voice can't drift toward a generic
                    // Scandinavian / Swedish accent on English loanwords or digits.
                    // Override via Voice:Locale in appsettings.json (set to empty
                    // string to opt out entirely).
                    var voiceLocale = configuration.GetValue<string>("Voice:Locale")
                        ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.DefaultVoiceLocale;
                    if (!string.IsNullOrWhiteSpace(voiceLocale))
                        voice["locale"] = voiceLocale;

                    // Pins the accent on English loanwords inside Danish sentences ("router",
                    // "streaming", "bredbånd"). Unset = unpredictable accent per the API reference.
                    var preferLocales = configuration.GetSection("Voice:PreferLocales").Get<string[]>()
                        ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.DefaultPreferLocales;
                    if (preferLocales.Length > 0)
                        voice["prefer_locales"] = preferLocales;
                    break;
            }

            logger.LogInformation("Voice config: type={Type}, name={Name}, locale={Locale}, temperature={Temp}, style={Style}",
                voiceType, voiceName,
                voice.ContainsKey("locale") ? voice["locale"] : (object)"(omitted)",
                voice.ContainsKey("temperature") ? voice["temperature"] : (object)"(omitted)",
                voice.ContainsKey("style") ? voice["style"] : (object)"(omitted)");

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
                // Config wins; otherwise fall back to the shared Norlys phrase list so caller-agent
                // and danish-voice-lab always send the same vocabulary boost to STT.
                var phrases = configuration.GetSection("Transcription:PhraseList").Get<string[]>();
                if (phrases is null || phrases.Length == 0)
                {
                    phrases = ContactCenterAgent.Shared.VoiceLive.NorlysDanishPhrases.All.ToArray();
                }
                config["phrase_list"] = phrases;
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
                            var audioBytes = Convert.FromBase64String(data["delta"].ToString()!);
                            if (m_greetingInFlight)
                            {
                                m_greetingFirstAudioUtc ??= DateTime.UtcNow;
                                m_greetingAudioBytesSent += audioBytes.Length;
                            }
                            // While waiting for the farewell, count bytes from any response
                            // OTHER than the hang_up response itself (the hang_up response
                            // emits no audio, so in practice all deltas after hang_up belong
                            // to the farewell). Used to compute realistic playback duration.
                            if (m_pendingHangUp)
                            {
                                m_farewellFirstAudioUtc ??= DateTime.UtcNow;
                                m_farewellAudioBytes += audioBytes.Length;
                            }
                            var jsonString = OutStreamingData.GetAudioDataForOutbound(audioBytes);
                            await m_mediaStreaming.SendMessageAsync(jsonString);
                        }
                        else if (msgType == "input_audio_buffer.speech_started")
                        {
                            if (m_greetingInFlight)
                            {
                                // Server has interrupt_response=false during greeting, so it
                                // won't auto-cancel TTS. We must mirror that on the client side
                                // — DO NOT send StopAudio or response.cancel, or the greeting
                                // dies anyway. Just log and let the greeting finish.
                                m_logger.LogInformation("VAD started during greeting — IGNORING (greeting protection)");
                            }
                            else
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

                                // Store the hang-up intent — we'll disconnect AFTER the farewell audio completes.
                                // The MANDATORY rule in the hang_up tool description and the
                                // # AFSLUTNING block in personas.json BOTH instruct the AI to speak the
                                // farewell BEFORE calling hang_up. With gpt-realtime-1.5 this is reliable,
                                // so the farewell audio has ALREADY played on the line by the time we get
                                // here. We just need to capture the response_id of the (audio-less) tool-call
                                // response so we can ignore its imminent response.done — the disconnect
                                // happens via the safety-net Task.Delay below.
                                m_pendingHangUp = true;
                                m_pendingHangUpReason = args ?? "AI initiated hang up";
                                m_hangUpResponseId = data.ContainsKey("response_id") ? data["response_id"]?.ToString() : null;
                                m_farewellAudioBytes = 0;
                                m_farewellDisconnectScheduled = false;

                                // Acknowledge the tool call with a JSON tool result. We do NOT chain a
                                // response.create asking for ANOTHER farewell — that was the cause of the
                                // double goodbye ("Tak for at være kunde hos Norlys ... [tool] ... Tak fordi
                                // du er kunde hos Norlys ..."). Trust the persona / tool description to have
                                // already produced the farewell.
                                var toolOutput = new
                                {
                                    type = "conversation.item.create",
                                    item = new
                                    {
                                        type = "function_call_output",
                                        call_id = callId,
                                        output = "{\"success\":true}"
                                    }
                                };
                                await SendMessageAsync(JsonSerializer.Serialize(toolOutput), CancellationToken.None);
                                m_logger.LogInformation("hang_up acknowledged — will disconnect on the safety-net timer (hang_up response_id: {ResponseId})", m_hangUpResponseId ?? "<unknown>");

                                // Safety net: the AI has already spoken its farewell BEFORE calling hang_up
                                // (per the MANDATORY rule in the tool description). Give PSTN time to actually
                                // play out the trailing audio, then disconnect. ~6s covers a typical Norlys
                                // farewell ("Tak for at være kunde hos Norlys, jeg ønsker dig en dejlig dag.")
                                // plus ACS RTP jitter buffer. If we ever observe the AI calling hang_up WITHOUT
                                // a preceding farewell on real calls, the right fix is to strengthen the prompt
                                // — NOT to chain a second response.create here (that's what was producing the
                                // double goodbye).
                                _ = Task.Run(async () =>
                                {
                                    const int FarewellPlaybackMs = 6000;
                                    await Task.Delay(FarewellPlaybackMs);
                                    if (m_pendingHangUp && !m_farewellDisconnectScheduled)
                                    {
                                        m_farewellDisconnectScheduled = true;
                                        m_logger.LogInformation("Disconnecting after {Delay}ms farewell-playback wait", FarewellPlaybackMs);
                                        if (m_onHangUp != null)
                                        {
                                            await m_onHangUp(m_pendingHangUpReason ?? "AI initiated hang up");
                                        }
                                    }
                                });
                            }
                        }
                        else if (msgType == "response.done")
                        {
                            // Log response.done detail for debugging premature disconnects
                            var responseDoneDetail = data.ContainsKey("response") ? data["response"]?.ToString() : "";

                            // Surfaced separately because response_detail is truncated at 1000 chars and
                            // usage sits after the output array. 0 on every turn = reasoning_effort is inert.
                            var reasoningTokens = "n/a";
                            if (data.ContainsKey("response") && data["response"] is JsonElement usageRespElem &&
                                usageRespElem.ValueKind == JsonValueKind.Object &&
                                usageRespElem.TryGetProperty("usage", out var usageElem) &&
                                usageElem.TryGetProperty("output_token_details", out var outDetailsElem) &&
                                outDetailsElem.TryGetProperty("reasoning_tokens", out var reasoningElem))
                            {
                                reasoningTokens = reasoningElem.ToString();
                            }

                            m_telemetryClient?.TrackEvent("VoiceLiveResponseDone", new Dictionary<string, string>
                            {
                                { "voice_live.pending_hangup", m_pendingHangUp.ToString() },
                                { "voice_live.reasoning_tokens", reasoningTokens },
                                { "voice_live.response_detail", responseDoneDetail?[..Math.Min(1000, responseDoneDetail?.Length ?? 0)] ?? "" },
                                { "chat.phone_number", m_phoneNumber ?? "" }
                            });

                            if (m_pendingHangUp)
                            {
                                // Identify which response just finished. The response that contained
                                // the hang_up tool call has no audio — its response.done arrives ~1ms
                                // after we send response.create for the farewell. We must ignore it and
                                // wait for the NEXT response.done (the farewell with audio).
                                string? thisResponseId = null;
                                try
                                {
                                    if (data.ContainsKey("response") && data["response"] is JsonElement respElem &&
                                        respElem.ValueKind == JsonValueKind.Object &&
                                        respElem.TryGetProperty("id", out var idElem))
                                    {
                                        thisResponseId = idElem.GetString();
                                    }
                                }
                                catch { /* best-effort id extraction */ }

                                if (!string.IsNullOrEmpty(m_hangUpResponseId) && thisResponseId == m_hangUpResponseId)
                                {
                                    m_logger.LogInformation("Ignoring response.done for hang_up tool-call response {ResponseId} — waiting for farewell response", thisResponseId);
                                    continue;
                                }

                                // This is the farewell response. Compute playback delay from the actual
                                // streamed audio bytes (same math as the greeting): Voice Live emits
                                // 24 kHz pcm16 = 48000 bytes/sec. Add a safety tail for ACS RTP jitter.
                                m_farewellDisconnectScheduled = true;
                                const int VoiceLiveBytesPerSecond = 24000 * 2;
                                const int SafetyTailMs = 500;
                                var bytesSnapshot = m_farewellAudioBytes;
                                var audioMs = (int)(bytesSnapshot * 1000L / VoiceLiveBytesPerSecond);
                                // Playback started at the first streamed frame, so only the remainder is left to wait.
                                var elapsedMs = m_farewellFirstAudioUtc is { } farewellStart
                                    ? (int)(DateTime.UtcNow - farewellStart).TotalMilliseconds
                                    : 0;
                                var playbackMs = Math.Max(audioMs - elapsedMs, 0) + SafetyTailMs;
                                // Floor guards a zero byte counter; ceiling: a Norlys farewell is never longer.
                                playbackMs = Math.Clamp(playbackMs, 1000, 8000);
                                m_logger.LogInformation(
                                    "Farewell response.done ({ResponseId}) — audio={AudioMs}ms, alreadyPlayed={ElapsedMs}ms, disconnecting in {Delay}ms ({Bytes} bytes)",
                                    thisResponseId ?? "<unknown>", audioMs, elapsedMs, playbackMs, bytesSnapshot);
                                await Task.Delay(playbackMs);

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
                            if (m_greetingInFlight && !m_greetingDelayScheduled)
                            {
                                // Schedule the protection-off session.update for AFTER the
                                // greeting actually finishes playing on the phone. response.done
                                // fires when the SERVER finishes generating audio (sub-second),
                                // not when PSTN has played it (~4s for typical greeting).
                                //
                                // Voice Live source audio is 24 kHz pcm16 = 48000 bytes/sec.
                                // Add a small safety tail (300 ms) for ACS RTP jitter buffer.
                                //
                                // m_greetingDelayScheduled prevents this from re-firing on the
                                // SECOND response.done (e.g. when AI replies to user's mid-greeting
                                // "yes" right after we re-enable create_response).
                                m_greetingDelayScheduled = true;
                                const int VoiceLiveBytesPerSecond = 24000 * 2;
                                const int SafetyTailMs = 300;
                                var bytesSnapshot = m_greetingAudioBytesSent;
                                var audioMs = (int)(bytesSnapshot * 1000L / VoiceLiveBytesPerSecond);
                                // Playback started at the first streamed frame, so only the remainder is left
                                // to wait. Holding the full duration from here adds dead air equal to however
                                // much of the greeting the phone has already played.
                                var elapsedMs = m_greetingFirstAudioUtc is { } greetingStart
                                    ? (int)(DateTime.UtcNow - greetingStart).TotalMilliseconds
                                    : 0;
                                var playbackMs = Math.Max(audioMs - elapsedMs, 0) + SafetyTailMs;
                                m_logger.LogInformation(
                                    "Greeting response.done; audio={AudioMs}ms, alreadyPlayed={ElapsedMs}ms, holding protection {DelayMs}ms ({Bytes} bytes)",
                                    audioMs, elapsedMs, playbackMs, bytesSnapshot);
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await Task.Delay(playbackMs);
                                        m_greetingInFlight = false;

                                        // Discard ANY audio the server captured during the greeting.
                                        // Without this, what the customer said while the greeting was
                                        // playing is committed as a user turn — and the moment we
                                        // re-enable normal turn detection, the server replies to it
                                        // instantly. Net effect: AI feels like it didn't wait.
                                        // Clearing the buffer makes the AI truly wait for fresh input.
                                        await SendMessageAsync(
                                            JsonSerializer.Serialize(new { type = "input_audio_buffer.clear" }, s_compactJson),
                                            CancellationToken.None);
                                        m_logger.LogInformation("Cleared input audio buffer (discarding any mid-greeting speech)");

                                        m_logger.LogInformation("Greeting playback estimated complete — re-enabling normal turn detection");
                                        await UpdateSessionAsync();

                                        // Do NOT fire response.create here — let the server's normal
                                        // turn detection trigger the next AI turn ONLY when the user
                                        // actually speaks fresh after the greeting. (Previous version
                                        // called response.create which made the AI reply instantly to
                                        // whatever was buffered during the greeting.)
                                    }
                                    catch (Exception ex)
                                    {
                                        m_logger.LogWarning(ex, "Failed to re-enable barge-in after greeting");
                                        m_greetingInFlight = false;
                                    }
                                });
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
                // Generate the one-shot structured case summary at teardown (call already
                // ended — off the farewell timing path). Fed to the outreach finalizer.
                if (!m_caseSummaryEmitted && m_analysisService != null && m_caseSummaryWriter != null)
                {
                    m_caseSummaryEmitted = true;
                    try
                    {
                        var summary = await m_analysisService.GenerateCaseSummaryAsync(m_systemPrompt);
                        if (summary != null) m_caseSummaryWriter.TryWrite(summary);
                    }
                    catch (Exception sumEx) { m_logger.LogWarning(sumEx, "Case summary generation failed"); }
                }

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

