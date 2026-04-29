# Test Danish HD Omni styles (Christel + Jeppe)

Run the Danish IMPRESS subset of the `danish-lab` command — `da-DK` HD Omni voices (Christel + Jeppe) at MAX expressiveness across the dramatic moods (`shouting`, `angry`, `panicked`, `terrified`, `urgent`). Synthesized via the **direct Speech SDK** (full 24 kHz, NOT Voice Live, NOT PSTN) and played through the default Windows speaker.

```pwsh
cd code/speech-tool
dotnet build
dotnet run --no-build -- danish-lab --only impress-da
```

Plays IDs `100-impress-da-shouting-max` → `106-impress-da-jeppe-angry` (7 clips, ~20 s).

Auth: `DefaultAzureCredential` (`az login` first). Endpoint: `cog-contactcenteragent` in Sweden Central.

## What you'll hear

| ID                              | Voice    | Style     | Note                                |
| ------------------------------- | -------- | --------- | ----------------------------------- |
| `100-impress-da-shouting-max`   | Christel | shouting  | Loud Danish complaint, max params   |
| `101-impress-da-angry-max`      | Christel | angry     | Cold furious billing dispute        |
| `102-impress-da-panicked`       | Christel | panicked  | Power-out emergency                 |
| `103-impress-da-terrified`      | Christel | terrified | Whispered fear                      |
| `104-impress-da-urgent`         | Christel | urgent    | Sharp, fast, no-nonsense            |
| `105-impress-da-jeppe-shouting` | Jeppe    | shouting  | Male voice — deeper, more intense   |
| `106-impress-da-jeppe-angry`    | Jeppe    | angry     | Cancellation rant, often intimidating |

All clips use HD Omni params at the docs' max: `temperature=1.0;top_p=1.0;top_k=50;cfg_scale=1.8`.

## Useful flags

```pwsh
dotnet run --no-build -- danish-lab --only impress-da --keep      # keep WAVs in %TEMP%\danish-voice-lab\
dotnet run --no-build -- danish-lab --only impress-da --pause-ms 2000
dotnet run --no-build -- danish-lab --only impress-da --list      # list without synthesizing
```

See [try-moods.prompt.md](./try-moods.prompt.md) for the full lab and how to add new variants.
