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
    /// Voice Live realtime endpoint API version. <c>2026-01-01-preview</c> unlocks
    /// <c>auto_truncate</c> on turn_detection, <c>pre_generated_assistant_message</c>,
    /// <c>interim_response</c> (cascaded mode only), word-level audio timestamps, and
    /// the <c>conversation: "none"</c> out-of-band response option. Wire format is
    /// backwards-compatible with the <c>2025-10-01</c> GA payload we already send, so
    /// fields we don't set continue to behave as before.
    /// </summary>
    public const string ApiVersion = "2026-01-01-preview";

    // ── Model / voice ─────────────────────────────────────────────────────────
    /// <summary>
    /// Voice Live model deployment name. <c>gpt-realtime-1.5</c> (released 2026-02-23)
    /// is the newest GA realtime audio-native model in the gpt-realtime family —
    /// drop-in replacement for <c>gpt-realtime</c> with lower first-token latency and
    /// improved Danish prosody. Same Pro pricing tier. Override per-environment via
    /// <c>AzureOpenAI:DeploymentName</c> in that surface's appsettings.json (e.g.
    /// fall back to <c>gpt-realtime</c> for A/B testing or <c>gpt-realtime-mini</c>
    /// for the cheaper Basic tier).
    /// </summary>
    public const string Model = "gpt-realtime-1.5";

    /// <summary>
    /// Production Danish voice. Single source of truth for both danish-voice-lab
    /// and caller-agent. To override per-environment, set Voice:Name in that
    /// surface's appsettings.json.
    ///
    /// Currently: Dragon HD Omni Christel — richer prosody than standard neural,
    /// requires HD-eligible region (Sweden Central ✓).
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
    /// mstts:express-as style applied to the voice (RealtimeAzureStandardVoice.style).
    /// Docs claim styles are English-only but lab-verified 29 Apr 2026 they DO affect
    /// da-DK HD Omni audibly. "friendly" picked as Norlys default after the 5-mood
    /// judge A/B (see .github/prompts/try-moods.prompt.md).
    ///
    /// ONLY meaningful on HD / HD Omni voices. Sent on the wire only when
    /// <see cref="IsHdVoice"/> returns true; standard neural voices ignore it (and
    /// unknown fields can trigger silent fallback to a default voice).
    /// </summary>
    public const string DefaultVoiceStyle = "friendly";

    /// <summary>
    /// True when <paramref name="voiceName"/> is an HD or HD Omni voice (contains
    /// ":DragonHD" suffix). Use this to gate sending the temperature field — standard
    /// neural voices don't accept it and the docs warn unsupported fields can cause
    /// silent fallback to a default voice.
    /// </summary>
    public static bool IsHdVoice(string voiceName) =>
        voiceName?.Contains(":DragonHD", System.StringComparison.OrdinalIgnoreCase) == true;

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
