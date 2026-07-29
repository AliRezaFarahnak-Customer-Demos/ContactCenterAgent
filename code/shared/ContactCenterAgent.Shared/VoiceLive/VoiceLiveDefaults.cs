namespace ContactCenterAgent.Shared.VoiceLive;

/// <summary>
/// Default Azure Voice Live session parameters. Both danish-voice-lab (laptop mic)
/// and caller-agent (PSTN via ACS) read from here so the wire payload stays in
/// lockstep — see app-architecture-dependencies notes: "Wire payload &gt; SDK choice".
///
/// Tuned by ear in the Danish lab; do not change without re-testing both surfaces.
/// </summary>
public static class VoiceLiveDefaults
{
    // ── Audio format ──────────────────────────────────────────────────────────
    /// <summary>24 kHz mono 16-bit PCM — what gpt-realtime emits and the lab uses.</summary>
    public const int SampleRate = 24_000;

    // ── Voice Live API version ────────────────────────────────────────────────
    /// <summary>
    /// Voice Live realtime endpoint API version. <c>2026-04-10</c> is the current GA
    /// version used across the official docs (including the BYOM endpoint examples).
    /// Behaviour change vs earlier versions: the DEFAULT <c>prefix_padding_ms</c> became
    /// 400 (server_vad) / 420 (azure_semantic_vad). We always send an explicit value
    /// (<see cref="VadPrefixPaddingMs"/>), so that default shift doesn't reach us.
    /// </summary>
    public const string ApiVersion = "2026-04-10";

    // ── Model / voice ─────────────────────────────────────────────────────────
    /// <summary>
    /// Voice Live model. <c>gpt-realtime-2.1</c> (2026-07-07) is the newest realtime
    /// audio model — per the model docs it brings "improved silence and noise handling"
    /// over gpt-realtime-2, which is exactly the PSTN failure mode (line noise causing
    /// false barge-ins) this codebase keeps fighting.
    ///
    /// IMPORTANT — this model is NOT in Voice Live's pre-deployed allowlist (that list
    /// still tops out at gpt-realtime-1.5, verified against the docs 2026-07-29). It only
    /// works through Bring Your Own Model: we deploy it into our own Foundry resource and
    /// connect with <c>?profile=</c><see cref="ByomProfile"/>. Sending it WITHOUT the
    /// profile parameter gets the connection closed with "Model ... is not supported in
    /// this region."
    ///
    /// Override per-environment via <c>AzureOpenAI:DeploymentName</c> (and clear
    /// <c>AzureOpenAI:ByomProfile</c>) to fall back to natively-hosted gpt-realtime-1.5.
    /// </summary>
    public const string Model = "gpt-realtime-2.1";

    /// <summary>
    /// Voice Live BYOM profile. Required because <see cref="Model"/> is a model we deploy
    /// ourselves rather than one Voice Live pre-hosts. Empty string = don't send the
    /// <c>profile</c> query parameter (correct for natively-supported models).
    ///
    /// Prerequisite: the Foundry resource's system-assigned managed identity needs the
    /// <c>Foundry User</c> role (53ca6127-db72-4b80-b1b0-d745d6d5456d) on itself, so the
    /// Voice Live service can reach our deployment for the life of a long session.
    /// </summary>
    public const string ByomProfile = "byom-azure-openai-realtime";

    /// <summary>
    /// Model for the mood-emoji + LLM-as-judge sidecar (<c>ConversationAnalysisService</c>).
    /// <c>gpt-5.6-luna</c> (2026-07-09) is the newest GPT-5.6 release available in Sweden
    /// Central and supports structured outputs, which the analysis schema depends on.
    ///
    /// Cost note: this runs per transcript line, so it's the hottest path in the app. If
    /// spend becomes a problem, override <c>AzureOpenAI:AnalysisDeploymentName</c> back to
    /// a nano-tier model — the structured-output schema is identical across both.
    /// Quota note: gpt-5.6 needs an explicit quota request below subscription Tier 5.
    /// </summary>
    public const string AnalysisModel = "gpt-5.6-luna";

    /// <summary>
    /// Production Danish voice. Single source of truth for both danish-voice-lab
    /// and caller-agent. To override per-environment, set Voice:Name in that
    /// surface's appsettings.json.
    ///
    /// Currently: Dragon HD Omni Christel — richer prosody than standard neural,
    /// requires HD-eligible region (Sweden Central ✓).
    ///
    /// Checked 2026-07-29 and deliberately NOT changed: neither of the newer TTS paths
    /// covers Danish. MAI-Voice-2-Flash ships 18 locales with no da-DK, and the
    /// `azure-realtime` native voice set (ava, emma, …) has no da-DK either. Dragon HD
    /// Omni remains the best available Danish voice.
    /// Alternatives:
    ///   • da-DK-Jeppe:DragonHDOmniLatestNeural   — male, HD Omni
    ///   • da-DK-ChristelNeural                  — standard neural (legacy, no temperature)
    ///   • da-DK-JeppeNeural                     — standard neural (male, legacy)
    /// Multilingual variants (*MultilingualNeural) are REJECTED by Voice Live azure-standard.
    /// </summary>
    public const string DefaultVoiceType = "azure-standard";
    public const string DefaultVoiceName = "da-DK-Christel:DragonHDOmniLatestNeural";

    /// <summary>
    /// Temperature for HD/HD Omni voices (0.0–1.0). Higher = more prosodic variation.
    /// 0.7 = calm customer-service default. SILENTLY IGNORED by standard neural voices.
    /// Null = omit the field entirely from the wire payload (use when voice is not HD).
    /// </summary>
    public const double DefaultVoiceTemperature = 0.7;

    /// <summary>
    /// Locale to ENFORCE on TTS output (RealtimeAzureStandardVoice.locale).
    /// Per the Voice Live API reference: "If not set, TTS may use a default accent
    /// based on text content." Without this, da-DK voices have been observed drifting
    /// toward a generic Scandinavian / Swedish accent on English loanwords, brand names,
    /// and digit sequences common in Norlys calls. Pinning locale="da-DK" eliminates that.
    /// </summary>
    public const string DefaultVoiceLocale = "da-DK";

    /// <summary>
    /// <c>prefer_locales</c> — pins the ACCENT used for embedded foreign words.
    /// Danish customer-service speech is full of English loanwords ("onboarding", "streaming",
    /// "router", "wifi", "bredbånd", "Stofa WebTV"), and per the API reference, if this isn't set
    /// "TTS uses default accent of each language" — i.e. the accent on those words is unpredictable.
    /// Listing da-DK first then en-US keeps Danish primary and makes loanwords American English
    /// rather than a random accent.
    ///
    /// Distinct from <see cref="DefaultVoiceLocale"/>: `locale` ENFORCES one language (and emits
    /// silence on mismatched text); `prefer_locales` only picks accents. They're complementary.
    /// </summary>
    public static readonly string[] DefaultPreferLocales = ["da-DK", "en-US"];

    /// <summary>
    /// <c>appended_text_after_truncation</c> — appended to the assistant transcript when the caller
    /// barges in, so the model KNOWS it was cut off instead of assuming the caller heard everything.
    /// Danish, because it lands in the Danish conversation context.
    ///
    /// Constraints: <c>azure_semantic_vad</c> / <c>_multilingual</c> ONLY, requires
    /// <c>interrupt_response: true</c>, and pairs with <c>auto_truncate</c>. Not in the typed SDK —
    /// raw JSON only (fine here; caller-agent builds the payload by hand).
    /// </summary>
    public const string TruncationNotice = " [Kunden afbrød mig her.]";

    /// <summary>
    /// mstts:express-as style applied to the voice (RealtimeAzureStandardVoice.style).
    ///
    /// CORRECTED 2026-07-29: the previous default "friendly" is NOT in the HD / HD Omni style
    /// vocabulary at all — it belongs to the STANDARD NEURAL set. It audibly changed da-DK output in
    /// the 29 Apr lab, but that's the model semantically reading the word, not a trained style token.
    /// "reassuring" is a documented HD Omni tag and the closest legal analogue for utility customer
    /// service. Both are selectable per call from the admin UI — see <see cref="AvailableStyles"/>.
    ///
    /// Docs caveat: "Styles are available on all English content" — Danish style support is
    /// undocumented either way, so treat any style as an A/B knob, not a guarantee.
    ///
    /// ONLY meaningful on HD / HD Omni voices. Sent on the wire only when
    /// <see cref="IsHdVoice"/> returns true; standard neural voices ignore it (and
    /// unknown fields can trigger silent fallback to a default voice).
    /// </summary>
    public const string DefaultVoiceStyle = "reassuring";

    /// <summary>A selectable voice or style option surfaced in the admin UI.</summary>
    public sealed record VoiceOption(string Value, string Label, string Description);

    /// <summary>
    /// Every da-DK voice that exists in Azure TTS — verified 2026-07-29 against the Dragon HD Omni
    /// catalog JSON and the Learn language-support table. There are exactly four; no DragonHD
    /// (non-Omni), HD Flash, MAI, or azure-realtime-native voice covers Danish.
    /// </summary>
    public static readonly VoiceOption[] AvailableVoices =
    [
        new("da-DK-Christel:DragonHDOmniLatestNeural", "Christel (HD Omni)", "Kvinde, voksen — poleret og professionel. Standard."),
        new("da-DK-Jeppe:DragonHDOmniLatestNeural",    "Jeppe (HD Omni)",    "Mand, ung voksen — blød og autoritativ."),
        new("da-DK-ChristelNeural",                    "Christel (neural)",  "Ældre standardstemme. Ingen temperatur/stil."),
        new("da-DK-JeppeNeural",                       "Jeppe (neural)",     "Ældre standardstemme, mand."),
    ];

    /// <summary>
    /// Style choices offered in the UI. The first five are documented HD Omni style tags; "friendly"
    /// is kept because it was lab-tuned by ear even though it isn't in the official vocabulary.
    /// Empty value = omit the field entirely.
    /// </summary>
    public static readonly VoiceOption[] AvailableStyles =
    [
        new("reassuring",   "Beroligende",  "Dokumenteret HD Omni-stil. Standard for Norlys."),
        new("calm",         "Rolig",        "Dokumenteret. Lavere energi, meget neutral."),
        new("confident",    "Selvsikker",   "Dokumenteret. Tydelig og bestemt."),
        new("encouraging",  "Opmuntrende",  "Dokumenteret. Varmere, mere positiv."),
        new("appreciative", "Anerkendende", "Dokumenteret. Taknemmelig tone."),
        new("friendly",     "Venlig (ældre)", "Ikke i HD Omni-listen, men lab-testet på dansk."),
        new("",             "Ingen stil",   "Udelad feltet helt — modellens egen prosodi."),
    ];

    /// <summary>
    /// True when <paramref name="voiceName"/> is an HD or HD Omni voice (contains
    /// ":DragonHD" suffix). Use this to gate sending the temperature field — standard
    /// neural voices don't accept it and the docs warn unsupported fields can cause
    /// silent fallback to a default voice.
    /// </summary>
    public static bool IsHdVoice(string voiceName) =>
        voiceName?.Contains(":DragonHD", System.StringComparison.OrdinalIgnoreCase) == true;

    // ── Reasoning ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Session-level <c>reasoning_effort</c> (Voice Live 2026-04-10 <c>RealtimeRequestSession</c>).
    /// Allowed: none | minimal | low | medium | high | xhigh.
    ///
    /// "minimal" is deliberate for PSTN: on a phone call perceived latency dominates quality,
    /// and the docs note reducing reasoning effort gives "faster responses and fewer tokens used
    /// on reasoning". The persona prompt does the heavy lifting, not chain-of-thought.
    ///
    /// Docs scope it to "reasoning models" — it is NOT confirmed that gpt-realtime-2.1 reasons at
    /// all. Verify empirically: <c>response.done</c> reports
    /// <c>usage.output_token_details.reasoning_tokens</c>. If that is always 0, this field is inert
    /// and can be dropped. Sending it is schema-valid either way, so it can't trigger the
    /// unsupported-field fallback that bites voice config.
    ///
    /// Override per-environment with <c>VoiceLive:ReasoningEffort</c>. Use "none" to disable
    /// rather than removing the field.
    /// </summary>
    public const string ReasoningEffort = "minimal";

    // ── VAD ───────────────────────────────────────────────────────────────────
    /// <summary>Server-side VAD type. azure_semantic_vad is what the lab uses.</summary>
    public const string VadType = "azure_semantic_vad";

    /// <summary>0.3 = aggressive (matches console). Raise toward 0.7 only if PSTN noise causes false barge-ins.</summary>
    public const double VadThreshold = 0.3;

    public const int VadPrefixPaddingMs = 300;

    /// <summary>
    /// End-of-turn silence in ms. 200 ms = very snappy turn-taking on PSTN.
    /// History: 500 ms (matched the danish-voice-lab console sandbox) → 300 ms
    /// (first latency pass) → 200 ms (current). Each step shaves perceived
    /// reply latency at the cost of more false barge-in risk on caller
    /// hesitations ("øhm…", pauses inside number/address reading). If false
    /// barge-ins become real, raise back toward 300 via
    /// VoiceLive:Vad:SilenceDurationMsOther in appsettings.json.
    /// </summary>
    public const int VadSilenceDurationMs = 200;

    // ── Audio cleanup ─────────────────────────────────────────────────────────
    public const string NoiseReductionType = "azure_deep_noise_suppression";
    public const string EchoCancellationType = "server_echo_cancellation";

    // ── Transcription (sidecar STT for the admin UI) ──────────────────────────
    public const string TranscriptionModel = "azure-speech";
    public const string TranscriptionLanguage = "da-DK";
}
