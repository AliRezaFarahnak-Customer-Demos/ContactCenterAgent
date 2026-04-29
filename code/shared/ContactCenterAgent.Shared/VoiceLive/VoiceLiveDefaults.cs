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

    /// <summary>Production Danish voice. Multilingual variants are rejected by Voice Live azure-standard.</summary>
    public const string DefaultVoiceType = "azure-standard";
    public const string DefaultVoiceName = "da-DK-ChristelNeural";

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
