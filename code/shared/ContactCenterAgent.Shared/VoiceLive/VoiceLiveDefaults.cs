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

    // ── Model / voice ─────────────────────────────────────────────────────────
    public const string Model = "gpt-realtime";

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
    public const int VadSilenceDurationMs = 500;

    // ── Audio cleanup ─────────────────────────────────────────────────────────
    public const string NoiseReductionType = "azure_deep_noise_suppression";
    public const string EchoCancellationType = "server_echo_cancellation";

    // ── Transcription (sidecar STT for the admin UI) ──────────────────────────
    public const string TranscriptionModel = "azure-speech";
    public const string TranscriptionLanguage = "da-DK";
}
