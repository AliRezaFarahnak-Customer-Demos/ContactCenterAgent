/**
 * GET /api/voice-options
 *
 * Danish voice + style choices for the call composer.
 *
 * Kept in sync by hand with ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults
 * (AvailableVoices / AvailableStyles / DefaultVoiceName / DefaultVoiceStyle) — the
 * SPA has no .NET backend to read them from.
 *
 * Verified 2026-07-29: these four are the ONLY da-DK voices in Azure TTS. No
 * DragonHD (non-Omni), HD Flash, MAI-Voice, or azure-realtime-native voice covers Danish.
 */
export default defineEventHandler(() => {
  return {
    defaultVoice: "da-DK-Christel:DragonHDOmniLatestNeural",
    defaultStyle: "reassuring",
    voices: [
      {
        value: "da-DK-Christel:DragonHDOmniLatestNeural",
        label: "Christel (HD Omni)",
        description: "Kvinde, voksen — poleret og professionel. Standard.",
        hd: true,
      },
      {
        value: "da-DK-Jeppe:DragonHDOmniLatestNeural",
        label: "Jeppe (HD Omni)",
        description: "Mand, ung voksen — blød og autoritativ.",
        hd: true,
      },
      {
        value: "da-DK-ChristelNeural",
        label: "Christel (neural)",
        description: "Ældre standardstemme. Ingen stil eller temperatur.",
        hd: false,
      },
      {
        value: "da-DK-JeppeNeural",
        label: "Jeppe (neural)",
        description: "Ældre standardstemme, mand.",
        hd: false,
      },
    ],
    // Only applied to HD voices — standard neural ignores style entirely.
    styles: [
      { value: "reassuring", label: "Beroligende", documented: true },
      { value: "calm", label: "Rolig", documented: true },
      { value: "confident", label: "Selvsikker", documented: true },
      { value: "encouraging", label: "Opmuntrende", documented: true },
      { value: "appreciative", label: "Anerkendende", documented: true },
      { value: "friendly", label: "Venlig", documented: false },
      { value: "", label: "Ingen stil", documented: true },
    ],
  };
});
