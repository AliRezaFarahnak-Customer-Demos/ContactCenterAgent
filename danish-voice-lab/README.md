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

All the knobs live at the top of [`Program.cs`](Program.cs) inside the `LabSettings` block:

| Setting              | Try changing it to…                                                                |
| -------------------- | ---------------------------------------------------------------------------------- |
| `Voice`              | `da-DK-JeppeNeural`, `da-DK-ChristelNeural`, `alloy`, `verse`, `marin`             |
| `Instructions`       | Give the AI a different persona — formal, kid-friendly, customer-support, etc.     |
| `Model`              | `gpt-4o-realtime-preview`, `gpt-realtime`, `gpt-realtime-1.5`                      |
| `VadThreshold`       | Lower (0.1) = picks up quieter speech; higher (0.6) = ignores background noise     |
| `VadSilenceDuration` | Shorter (200ms) = AI interrupts faster; longer (1000ms) = waits for full sentences |

Edit, save, `dotnet run` again.

## Going to the phone

Once a config feels right here, copy the equivalent values into:

- `code/caller-agent/agent/AzureVoiceLiveService.cs` — model, voice, VAD
- `code/caller-agent/agent/appsettings.json` — instructions / system prompt

Note the phone uses **8 kHz G.711** (PSTN) instead of the **24 kHz PCM16** used here, so transcription quality on the phone will be lower than on your laptop — but the _behaviour_ (persona, turn-taking) ports 1:1.

## Auth

Uses `DefaultAzureCredential` → run `az login` once and you're set. The Cognitive Services endpoint in `appsettings.json` is the one provisioned by `azd up` for this repo.
