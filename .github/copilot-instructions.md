# Contact Center Agent — Copilot Instructions

Multi-agent platform: Azure AI Foundry + Azure Container Apps. Admin chat UI (Nuxt 3 + .NET 10) + Caller Agent (ACS + Voice Live API, .NET 8). `azd up` allowed locally.

> **Detailed architecture, telemetry, data flows, and gotchas** → see `.github/agents/app-architecture-dependencies.agent.md`

## Codebase Structure

```
code/
  admin-chat/          # Nuxt 3 SPA + .NET 10 backend (AG-UI + MS Agent Framework)
    agent/             # .NET 10 backend
    pages/             # Vue pages (index.vue = 3-column layout)
    components/        # Vue components (Chat, Analysis, Transcription, CallLog, CallComposer, etc.)
    composables/       # Vue composables (useAgentChat, useCallAnalysis, useCallLog, etc.)
    server/api/        # Nuxt server routes (SSE proxies to .NET + caller-agent)
    assets/css/        # Tailwind + brand styles
    public/            # Favicon (official Norlys CVI), logos, OG image
    plugins/           # App Insights browser SDK
  caller-agent/        # .NET 8 — ACS + Voice Live API (outbound/inbound AI phone calls)
    agent/             # CallerAgent, AcsMediaStreamingHandler, VoiceLive, ConversationAnalysis
  console-demo-voicelive/  # Local-only Voice Live CLI demo (.NET 10)
  speech-tool/         # Local-only TTS CLI (.NET 8)
scripts/               # provision-phone-number, setup-oidc, setup-eventgrid
infra/                 # Bicep (main.bicep → resources.bicep)
```

## Local Development

```bash
# Admin Chat (port 3000 + 8000)
cd code/admin-chat && npm install && npm run dev

# Caller Agent (port 5000) — requires devtunnel + ASPNETCORE_ENVIRONMENT=Development
cd code/caller-agent/agent && dotnet run --urls "http://localhost:5000"
```

- **NEVER build Docker locally** (ARM64 dev machine). Use `azd deploy` or `az acr build`.
  - `azd up` / `azd deploy` use **remote ACR build by default** — the build runs in Azure, but the source tarball is uploaded from the dev machine. On flaky internet, the upload is the bottleneck, not the build.
  - Keep `.dockerignore` tight in each service (`node_modules`, `bin/`, `obj/`, `.nuxt/`, `.output/`, `dist/`, `.git/`) to keep uploads small.
  - Fallback for unstable connections: `az acr build -r <acr> -t <image>:latest -f <Dockerfile> <context>` (streams logs, no azd state).
- **azd ≥ 1.24.1** required (1.23.6 has BlobNotFound bug).
- Set `ASPNETCORE_ENVIRONMENT=Development` for user-secrets to load.

## Deploy

```bash
azd up                        # provision + deploy both services
azd deploy admin-chat         # redeploy SPA only
azd deploy caller-agent       # redeploy phone agent only (e.g. after voice config tweak)
```

### Version stamping (admin-chat SPA)

- `code/admin-chat/.version.json` is regenerated automatically on every `azd up` / `azd deploy <svc>` by the **root-level `prepackage` hook** in `azure.yaml`. It writes `1.0.<git-commit-count>-<short-sha>` (e.g. `1.0.19-bdeba07`) and the SPA reads it via `nuxt.config.ts` runtimeConfig.
- The hook is **root-level on purpose** — service-scoped `services.admin-chat.hooks.prepackage` was observed to silently skip in some flows. Do not move it.
- `continueOnError: false` — if git is missing or the file write fails, the deploy fails loudly. That's intentional.
- If the deployed UI shows an old version after deploy: it's almost always **browser cache**. Hard-reload (`Ctrl+Shift+R`) or open incognito. Only if incognito still shows the old version, check `git log` of `.version.json` and rerun `azd deploy admin-chat`.

### Cache busting after deploy

The Nuxt SPA + service worker aggressively cache assets. After every deploy:

1. Hard reload (`Ctrl+Shift+R` / `Ctrl+F5`)
2. If still old → DevTools → Network → "Disable cache" → reload
3. Or open in incognito (bypasses SW + cache)

---

## Norlys Branding (CVI 2025)

Read the official guidelines **before** editing anything visual in `code/admin-chat/`.

### Design Guidelines

| Topic            | URL                                                                          |
| ---------------- | ---------------------------------------------------------------------------- |
| Hub              | https://norlys.design/                                                       |
| Visuel identitet | https://norlys.design/document/307                                           |
| Logo             | https://norlys.design/document/307#/grundelementer/logo                      |
| Farver           | https://norlys.design/document/307#/grundelementer/farver                    |
| Typografi        | https://norlys.design/document/307#/grundelementer/typografi                 |
| Ikonografi       | https://norlys.design/document/307#/grundelementer/ikonografi                |
| Digital design   | https://norlys.design/document/332                                           |
| Layout           | https://norlys.design/document/332#/indhold-og-layout/sidestruktur-og-layout |
| Content blokke   | https://norlys.design/document/332#/indhold-og-layout/content-blokke         |
| Ikoner (403 stk) | https://norlys.design/document/295                                           |

### Color Tokens (in `tailwind.config.ts`)

Use `norlys-*` tokens — never hardcode hex.

| Token                    | Hex                   | Use                                          |
| ------------------------ | --------------------- | -------------------------------------------- |
| `norlys-red`             | `#ed0812`             | Logo, primary CTA, splash, focus ring        |
| `norlys-red-2`           | `#d80812`             | Hover/active                                 |
| `norlys-red-3`           | `#c10000`             | Pressed state                                |
| `norlys-petroleum`       | `#0c4c4e`             | Headlines on light, secondary buttons, icons |
| `norlys-petroleum-2`     | `#004547`             | Hover petroleum                              |
| `norlys-petroleum-3`     | `#023a3c`             | Strongest headlines on light                 |
| `norlys-light-petroleum` | `#dbe7e4`             | Borders, dividers, disabled surfaces         |
| `norlys-sand`            | `#f4f2ec`             | Default page background                      |
| `norlys-sand-2` / `-3`   | `#ece9e0` / `#e0dbcd` | Alt surfaces                                 |
| `norlys-ink` (Warm Grey) | `#413f3c`             | Body text on light surfaces                  |

**Text rule:** Warm Grey on light surfaces; white/Sand on Red or Dark Petroleum.
**Red rule:** Present but never dominant — logo, CTAs, focus rings, small accents only.

### Typography

| Use       | Class           | Stack                                       | Rule                               |
| --------- | --------------- | ------------------------------------------- | ---------------------------------- |
| Headlines | `font-headline` | Norlys Headline → Georgia → Times New Roman | **Bold only, always left-aligned** |
| Body      | `font-body`     | Norlys Text → Arial → Helvetica → system-ui | Regular default                    |

- **Never use `font-mono`** on production UI. Use `tabular-nums` for aligned digits.
- Fonts gated behind norlys.design login — falls back to Georgia/Arial.

### Logo Placement

- **Top-right** or **bottom-left** only. Respect distance ≥ 1× brand "O".
- Positive (Red) on light surfaces; negative (Sand) on dark/Red surfaces.
- Logo files: `public/norlys-logo.svg` (positive), `public/norlys-logo-neg.svg` (negative).

### Favicon

Official Norlys "Brand O" favicon set — **do NOT replace**:
`favicon.ico`, `favicon-16x16.png`, `favicon-32x32.png`, `apple-touch-icon.png`, `android-chrome-192x192.png`, `android-chrome-512x512.png`

### Accessibility (WCAG)

- ≥ 4.5:1 contrast body text, ≥ 3:1 large text/icons
- Focus rings (`norlys-red/20`), `alt` text, `aria-label`, real `<label>` elements
- Don't convey state with color alone; respect `prefers-reduced-motion`

### Tone of Voice

Danish UI: enkelhed, handlekraft, optimisme. English for code/errors.

---

## Future Improvements (backlog)

### Better phone-call transcripts in admin UI

The transcripts shown in the admin chat (Voice Live `input_audio_transcription` →
`whisper-1`) often look poor on real phone calls because PSTN audio is **8 kHz
G.711 narrowband** — Whisper / gpt-4o-transcribe were trained mostly on
wideband mic audio, so WER roughly doubles on telephony. The same models
look great in `console-demo-voicelive/` because that uses the laptop mic at
24 kHz.

Note: this only affects the **displayed transcript**. `gpt-realtime-1.5` has its
own native multilingual ASR and usually understands the caller correctly even
when the whisper sidecar transcript is garbled (per the official Realtime API
spec: "the transcript can diverge somewhat from the model's interpretation, and
should be treated as a rough guide").

**Possible fix:** fork the ACS audio stream in `AcsMediaStreamingHandler` and
run a parallel **Azure AI Speech (Speech-to-Text) telephony recognizer** for
the admin-UI transcript only. Azure Speech has a dedicated 8 kHz telephony
model trained for Danish call-center audio and would give "console-app
quality" transcripts on the phone UI. Voice Live's `input_audio_transcription`
field does NOT accept `azure-speech` when the model is `gpt-realtime`, so the
parallel-stream approach is the only way.

Estimated effort: a few hundred LOC + extra Speech resource cost.
