# Try Danish TTS moods / styles

Run the `danish-lab` command in `code/speech-tool/` to A/B Danish HD Omni voices, styles (`mstts:express-as`), paralinguistics (`[sighing]`, `[laughter]`, `[breathing]`), temperatures, and English voices speaking Danish — synthesized via the **direct Speech SDK** (NOT Voice Live, NOT PSTN — this is laptop-speaker quality, full bandwidth) and played back through the default Windows speaker via `System.Media.SoundPlayer.PlaySync`.

## Quick reference

```pwsh
cd code/speech-tool

# List all variants without synthesizing
dotnet run -- danish-lab --list

# Play everything (~25 clips, ~2 min total)
dotnet run -- danish-lab

# Filter by id substring (case-insensitive)
dotnet run -- danish-lab --only style          # just the style A/B set (20-23)
dotnet run -- danish-lab --only paralinguistic # just sigh/laugh/breath (30-32)
dotnet run -- danish-lab --only cs-            # customer-service set (60-70)
dotnet run -- danish-lab --only narrated       # finalists with Danish self-intro (80-82)
dotnet run -- danish-lab --only judge          # 5-mood judge set: normal, friendly, professional, customer-service, shouting (90-94)
dotnet run -- danish-lab --only ava            # English voices speaking Danish (40-42)

# Keep WAVs in %TEMP%\danish-voice-lab\ for closer comparison or sharing
dotnet run -- danish-lab --keep

# Adjust pause between clips
dotnet run -- danish-lab --pause-ms 2000
```

Auth: `DefaultAzureCredential` (must be `az login`'d). Defaults: `cog-contactcenteragent` AI Services endpoint in Sweden Central (already set in `Program.cs`).

## Variant groups (see `code/speech-tool/Program.cs` for the actual SSML)

| Range   | What it tests                                                                                                                                                                                | Why                                                                                                    |
| ------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| `01–03` | Baselines: Christel HD Omni, Christel **standard** neural (current prod), Jeppe HD Omni                                                                                                      | A/B HD vs standard, female vs male                                                                     |
| `10–11` | Christel HD with `temperature=0.3` and `=1.0`                                                                                                                                                | Stability vs expressiveness sweep                                                                      |
| `20–23` | Christel HD + `style="empathetic" / "cheerful" / "sad" / "calm"`                                                                                                                             | Confirm styles audibly affect Danish (docs say English-only — they're wrong, lab-verified 29 Apr 2026) |
| `30–32` | Christel HD with inline `[sighing] / [laughter] / [breathing]`                                                                                                                               | Confirm paralinguistics fire on Danish                                                                 |
| `40–42` | Ava / Andrew (en-US HD Omni) speaking Danish text, with and without `<lang xml:lang='da-DK'>`                                                                                                | How bad does an English voice sound on Danish?                                                         |
| `50`    | Christel HD + `[sighing]` + `temperature=1.0`                                                                                                                                                | Most expressive Danish setup we can do via SSML                                                        |
| `60–70` | Customer-service A/B: same Norlys greeting across `customer-service`, `friendly`, `empathetic`, `calm`, `cheerful`, `chat`, `professional`, `newscast` styles + temp variant + Jeppe variant | Pick the right call-center voice persona                                                               |
| `80–82` | NARRATED finalists: Christel HD Omni with Danish self-intro ("Nu lyder jeg venlig…", "…professionel…", "…som en kundeservicemedarbejder…")                                                   | Final pick — voice announces the style so you can identify it without checking labels                  |
| `90–94` | JUDGE set (5 clips): `normal`, `friendly`, `professional`, `customer-service`, `shouting` — same Norlys greeting, each prefixed with "Nu lyder jeg X" / "Nu råber jeg!"                      | Quick A/B/C/D/E for picking a default style                                                            |

## Adding new style/mood variants

1. Open `code/speech-tool/Program.cs`, find the `variants = new[]` array in the `danish-lab` handler.
2. Add a `new Variant("ID", "DESCRIPTION", DanishLabHelpers.BuildSsmlExpressAs(...))` entry. Use a numeric ID prefix that fits the group (e.g. `90-` for new experiments).
3. Helper signatures in `DanishLabHelpers`:
   - `BuildSsml(xmlLang, voice, escapedText, parameters?)` — plain text
   - `BuildSsmlExpressAs(xmlLang, voice, escapedText, style, parameters?)` — with `mstts:express-as`
   - `BuildSsmlWithLang(outerXmlLang, voice, innerXmlLang, escapedText)` — code-switch via `<lang>`
   - `BuildSsmlMixed(voice)` — example: empathetic English then Danish via `<lang>`
4. Wrap the spoken text with `EscapeXml(...)`.
5. Build & run: `dotnet build; dotnet run --no-build -- danish-lab --only <your-prefix>`

Example: shouting + angry on Christel HD with a Danish self-intro:

```csharp
new Variant("91-narrated-shouting",
    "NARRATED: 'shouting' on Christel HD Omni",
    DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
        EscapeXml("Nu råber jeg! Hej Mette, jeg ringer fra Norlys!"),
        style: "shouting")),
```

## Reference: voice constants used in the lab

```csharp
const string ChristelHD       = "da-DK-Christel:DragonHDOmniLatestNeural"; // Danish female HD Omni
const string JeppeHD          = "da-DK-Jeppe:DragonHDOmniLatestNeural";    // Danish male HD Omni
const string ChristelStandard = "da-DK-ChristelNeural";                    // current prod (standard neural)
const string AvaHD            = "en-US-Ava:DragonHDOmniLatestNeural";      // English female HD Omni
const string AndrewHD         = "en-US-Andrew:DragonHDOmniLatestNeural";   // English male HD Omni
```

## Reference: full style list (HD Omni — all confirmed audible on da-DK)

`amazed, amused, angry, annoyed, anxious, appreciative, calm, cautious, cheerful, concerned, confident, confused, curious, customer-service, defeated, defensive, defiant, determined, disappointed, disgusted, doubtful, ecstatic, empathetic, encouraging, excited, fast, fearful, friendly, frustrated, happy, hesitant, hurt, impatient, impressed, intrigued, joking, laughing, newscast, optimistic, painful, panicked, panting, pleading, professional, proud, quiet, reassuring, reflective, relieved, remorseful, resigned, sad, sarcastic, secretive, serious, shocked, shouting, shy, skeptical, slow, struggling, surprised, suspicious, sympathetic, terrified, upset, urgent, whispering`

Paralinguistics (inline tokens, all languages): `[laughter] [coughing] [throat_clearing] [breathing] [sighing] [yawning]`

## Important caveat: lab vs PSTN

The lab uses the **Speech SDK directly** (full SSML, full 24 kHz bandwidth). What works here is **not automatically what reaches the actual phone call** — the `caller-agent` path goes through Voice Live, which only exposes a subset of fields on `RealtimeAzureStandardVoice`. To use a winning style on a real call:

1. Add a `style` field to the `voice` JSON in `BuildVoiceConfig` inside `code/caller-agent/agent/AzureVoiceLiveService.cs`.
2. Surface it via a config key (e.g. `Voice:Style` in `appsettings.json`).
3. Validate over PSTN — G.711 narrowband may shrink the perceived difference vs the lab.
4. Confirm via App Insights `Session accepted by server` trace that `session.updated.voice.style` echoes what you sent (otherwise = silent fallback).

See `.github/copilot-instructions.md` (Voice quality + Deferred customizations sections) for current production wiring.
