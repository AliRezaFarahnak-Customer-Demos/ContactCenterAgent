# Test English HD Omni styles (Ava + Andrew)

Run the English IMPRESS subset of the `danish-lab` command — `en-US` HD Omni voices (Ava + Andrew) at MAX expressiveness across the dramatic moods. Per the docs, **automatic style prediction is documented for `en-US-Ava` and `en-US-Andrew`** — these clips show HD Omni's strongest possible style adherence. Use them when you want to impress Microsoft customers.

Synthesized via the **direct Speech SDK** (full 24 kHz, NOT Voice Live, NOT PSTN) and played through the default Windows speaker.

```pwsh
cd code/speech-tool
dotnet build
dotnet run --no-build -- danish-lab --only impress-en
```

Plays IDs `110-impress-en-ava-shouting` → `120-impress-en-ava-range-calm-to-shouting` (6 clips, ~20 s). The closer (`120`) is a single clip where Ava swings from calm reassurance into full shouting mid-sentence — best demo of emotional range in one breath.

Auth: `DefaultAzureCredential` (`az login` first). Endpoint: `cog-contactcenteragent` in Sweden Central.

## What you'll hear

| ID                                         | Voice  | Style    | Note                                            |
| ------------------------------------------ | ------ | -------- | ----------------------------------------------- |
| `110-impress-en-ava-shouting`              | Ava    | shouting | Peak expressiveness, female                     |
| `111-impress-en-ava-angry`                 | Ava    | angry    | Calmer rage, very cinematic                     |
| `112-impress-en-andrew-shouting`           | Andrew | shouting | Male peak, intimidating                         |
| `113-impress-en-andrew-angry`              | Andrew | angry    | Worn-out fury                                   |
| `114-impress-en-ava-panicked`              | Ava    | panicked | Emergency / IVR demo                            |
| `120-impress-en-ava-range-calm-to-shouting` | Ava    | shouting | **Closer**: calm → shouting flip in one breath  |

All clips use HD Omni params at the docs' max: `temperature=1.0;top_p=1.0;top_k=50;cfg_scale=1.8`.

## Useful flags

```pwsh
dotnet run --no-build -- danish-lab --only impress-en --keep      # keep WAVs in %TEMP%\danish-voice-lab\
dotnet run --no-build -- danish-lab --only impress-en --pause-ms 2000
dotnet run --no-build -- danish-lab --only impress-en --list      # list without synthesizing
```

See [try-moods.prompt.md](./try-moods.prompt.md) for the full lab and how to add new variants.
