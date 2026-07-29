# Danish Voice Lab 🎙️🇩🇰

A tiny .NET 10 console app to **speak Danish to Azure Voice Live and hear it speak Danish back** — straight from your laptop mic and speakers.

It's a sandbox: tweak the voice, system prompt, VAD sensitivity, etc. in `Program.cs`, hear the result instantly, then port the good settings into `code/caller-agent/` so the phone behaves the same way.

## Run

```pwsh
az login
cd danish-voice-lab
dotnet run
```

Speak Danish. Press `Ctrl+C` to quit.

## What to tweak

Defaults live in `code/shared/ContactCenterAgent.Shared/VoiceLive/VoiceLiveDefaults.cs` (shared with
caller-agent). Override them for the lab only in [`appsettings.json`](appsettings.json):

| Setting              | Try changing it to…                                                                 |
| -------------------- | ----------------------------------------------------------------------------------- |
| `Voice`              | `da-DK-Jeppe:DragonHDOmniLatestNeural`, `da-DK-ChristelNeural`, `da-DK-JeppeNeural` |
| `Voice:Style`        | HD Omni styles: `reassuring`, `calm`, `confident`, `encouraging`, `appreciative`    |
| `Voice:Temperature`  | 0.0–1.0 — HD voices only. 0.7 = calm service; 0.8 = more variation                  |
| `PersonaId`          | Any id in the root `personas.json`                                                  |
| `VadThreshold`       | Lower (0.1) = picks up quieter speech; higher (0.6) = ignores background noise      |
| `VadSilenceDuration` | Shorter (200ms) = AI interrupts faster; longer (1000ms) = waits for full sentences  |

Those four voice names are the **only** `da-DK` voices in Azure TTS — there is no Danish
DragonHD (non-Omni), MAI-Voice, or `azure-realtime-native` voice. Style/temperature apply to the
HD Omni pair only; the plain `*Neural` voices ignore both.

Edit, save, `dotnet run` again.

## Going to the phone

Once a config feels right here, promote it by editing the shared defaults in
`code/shared/ContactCenterAgent.Shared/VoiceLive/VoiceLiveDefaults.cs` — caller-agent and this lab
both read from it, so one edit covers both. Only pin a prod-specific override in
`code/caller-agent/agent/appsettings.json` (`Voice:Name` / `Voice:Temperature` / `Voice:Style`) when
prod should deliberately differ from the lab. Redeploy with `azd deploy caller-agent`.

Note the phone uses **8 kHz G.711** (PSTN) instead of the **24 kHz PCM16** used here, so transcription quality on the phone will be lower than on your laptop — but the _behaviour_ (persona, turn-taking) ports 1:1.

## Auth

Uses `DefaultAzureCredential` → run `az login` once and you're set. The Cognitive Services endpoint in `appsettings.json` is the one provisioned by `azd up` for this repo.
