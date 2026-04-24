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
- **azd ≥ 1.24.1** required (1.23.6 has BlobNotFound bug).
- Set `ASPNETCORE_ENVIRONMENT=Development` for user-secrets to load.

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
