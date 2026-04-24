# Contact Center Agent — Copilot Instructions

> **`azd up` is allowed locally.** You may run `azd up`, `azd deploy`, and `azd provision` directly from the dev machine. The GitHub Action remains the canonical CI/CD path, but local deploys are fine for fast iteration.
>
> **Custom domain**: managed via the registrar control panel at `<your-domain-registrar-control-panel>` (Playwright can drive the UI when needed).

## Reference Documentation

| Topic                            | URL                                                                                                                            |
| -------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| Agent Framework docs             | https://learn.microsoft.com/en-us/agent-framework/                                                                             |
| Agent Framework GitHub           | https://github.com/microsoft/agent-framework                                                                                   |
| Agent Framework samples (Python) | https://github.com/microsoft/agent-framework/tree/main/python/samples                                                          |
| Agent Framework samples (.NET)   | https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted                                           |
| Agent Framework .NET SDK (NuGet) | https://www.nuget.org/profiles/MicrosoftAgentFramework                                                                         |
| AG-UI protocol (Python)          | https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/?pivots=programming-language-python                       |
| AG-UI frontend tools             | https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/frontend-tools?pivots=programming-language-python         |
| AG-UI backend tool rendering     | https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/backend-tool-rendering?pivots=programming-language-python |
| AG-UI SDK docs                   | https://docs.ag-ui.com/sdk/python/core/overview                                                                                |
| AG-UI GitHub                     | https://github.com/ag-ui-protocol/ag-ui                                                                                        |
| @aivue/chatbot (Vue)             | https://www.npmjs.com/package/@aivue/chatbot                                                                                   |
| @aivue/analytics (Vue)           | https://www.npmjs.com/package/@aivue/analytics                                                                                 |

## Purpose

A multi-agent management platform powered by Azure AI Foundry and Azure Container Apps. Two initial components:

1. **Contact Center Agent Admin** — Admin web UI for overseeing and managing deployed agent apps (status, logs, configuration).
2. **Agent Apps** — Individual agent applications (e.g., Weather Agent) deployed as standalone Container Apps, managed through the admin dashboard.

---

## Tenant & Accounts

**Owner:** _(set by your organization)_

| Field           | Value                                             |
| --------------- | ------------------------------------------------- |
| Tenant ID       | _(run `az account show --query tenantId -o tsv`)_ |
| Subscription ID | _(run `az account show --query id -o tsv`)_       |
| Admin UPN       | _(your Azure admin account)_                      |

> **Tenant-specific values** (subscription IDs, phone numbers, voice profile IDs, custom domain, dev tunnel URL) are NEVER committed. They are written by `azd env set` into `.azure/<env>/.env`, which is gitignored. Use the same pattern for any new per-tenant fact.

- Fully automated: `azd up` provisions everything, `azd down` tears down.

---

## Solution Architecture

| Layer      | Technology                                                     |
| ---------- | -------------------------------------------------------------- |
| Runtime    | Azure Container Apps (.NET 10 backend + Nuxt 3 Vue frontend)   |
| AI Backend | Azure AI Foundry (`AIServices` kind, S0) — GPT-5.4-nano        |
| Frontend   | Vue 3 + Nuxt 3 (direct AG-UI protocol over SSE, no CopilotKit) |
| Storage    | Azure Storage Account (Table Storage for agent data)           |
| IaC        | Bicep (subscription-scoped `main.bicep` → `resources.bicep`)   |
| Region     | `swedencentral`                                                |

### Azure Resources (provisioned by Bicep)

| Resource           | Name                          | Notes                                                  |
| ------------------ | ----------------------------- | ------------------------------------------------------ |
| Resource Group     | `rg-contactcenteragent`       | All resources under one RG                             |
| AI Foundry         | `cog-contactcenteragent`      | `AIServices` kind, S0, Managed Identity                |
| AI Foundry Project | `cog-contactcenteragent-prj`  | Child of AI Foundry account                            |
| Model Deployment   | `gpt-5.4-nano`                | GPT-5.4-nano, GlobalStandard, capacity 1000            |
| Model Deployment   | `gpt-realtime`                | gpt-realtime-1.5 (2026-02-23), GlobalStandard, cap 10  |
| Storage Account    | `stcontactcenteragent`        | Standard_LRS, Table Storage                            |
| Container Registry | `crcontactcenteragent`        | Standard SKU, admin enabled                            |
| Log Analytics      | `log-contactcenteragent`      | PerGB2018, 30-day retention                            |
| Container Apps Env | `cae-contactcenteragent`      | Logs to Log Analytics                                  |
| App Insights       | `appi-contactcenteragent`     | Shared telemetry for both agents                       |
| ACS                | `acs-contactcenteragent`      | Phone numbers, call routing                            |
| EventGrid Topic    | `evgt-contactcenteragent-acs` | ACS system topic (routes IncomingCall to caller agent) |
| Container App      | `ca-admin-chat`               | Admin Chat Agent (.NET 10 + Nuxt 3)                    |
| Container App      | `ca-caller-agent`             | Caller Agent (.NET 8, ACS + Realtime)                  |

### Live URLs

| Service      | URL                                                                 |
| ------------ | ------------------------------------------------------------------- |
| Admin Chat   | https://<your-custom-domain> (custom domain)                        |
| Admin Chat   | https://ca-admin-chat.<env-domain>.<region>.azurecontainerapps.io   |
| Caller Agent | https://ca-caller-agent.<env-domain>.<region>.azurecontainerapps.io |

---

## Codebase Structure

```
code/
  admin-chat/                 # Admin Chat Agent app
    agent/                    # .NET 10 backend (Microsoft Agent Framework + AG-UI)
      AdminChatAgent.cs       # Agent factory, tools, chat client setup, ChatEventTracker
      AdminChat.csproj        # Project file with AG-UI + Azure packages (Azure.AI.OpenAI v2.8.0-beta.1)
      appsettings.json        # Config (Azure OpenAI endpoint, model name)
    app.vue                   # Root Vue component
    nuxt.config.ts            # Nuxt 3 configuration (ssr: false = SPA mode)
    pages/
      index.vue               # Main page — 3-column layout (Analysis | Chat | Transcription)
    components/
      ChatMessage.vue          # Chat message component (markdown + streaming)
      ActivityPanel.vue        # Debug/session activity sidebar (hidden by default with v-if="false")
      TranscriptionPanel.vue   # Live call transcript sidebar
      AnalysisPanel.vue        # Real-time 15-category conversation analysis bars (GPT-5.4-nano)
      CallLogPanel.vue         # Live call log sidebar (inbound/outbound calls via SSE)
    composables/
      useAgentChat.ts          # AG-UI SSE client composable
      useCallTranscription.ts  # Live call transcription via SSE
      useCallAnalysis.ts       # Live conversation analysis via SSE (15 categories, scores 0-5)
      useCallLog.ts            # Live call log events via SSE
    plugins/
      appinsights.client.ts    # App Insights browser SDK (auto page views, Vue errors, exceptions)
    public/
      favicon.ico              # Multi-size favicon (16/32/48px) from octopus.png
      favicon.png              # 48x48 PNG favicon
      og-image.png             # 1200x630 OG image for social sharing (LinkedIn, Twitter)
    server/
      api/
        agent.post.ts          # Proxy route → .NET agent
        transcription/
          [contextId].get.ts   # SSE proxy → caller agent transcription stream
        analysis/
          [contextId].get.ts   # SSE proxy → caller agent analysis stream
        calls/
          stream.get.ts        # SSE proxy → caller agent call log stream
    assets/css/
      main.css                 # Tailwind + dark theme
    package.json              # Nuxt 3 + Vue 3 + App Insights dependencies
    tsconfig.json
  caller-agent/               # Caller Agent app (outbound AI phone calls)
    Dockerfile                # Multi-stage .NET 8 build + runtime
    agent/
      CallerAgent.cs           # API endpoints, ACS call handling, call log broadcast, per-call state (channels, prompts)
      AcsMediaStreamingHandler.cs  # WebSocket ↔ ACS media stream bridge
      AzureVoiceLiveService.cs     # Voice Live API 2025-10-01 (raw WebSocket, configurable voice)
      ConversationAnalysisService.cs # GPT-5.4-nano structured output analysis (15 categories, scored 0-5)
      Helper.cs                # EventGrid parsing, caller ID extraction
      CallerAgent.csproj       # .NET 8 Web SDK project (Azure.AI.OpenAI v2.1.0)
      appsettings.json         # Config (ACS, Azure OpenAI, phone number, Voice, AnalysisDeploymentName)
  console-demo-voicelive/     # Console demo for Voice Live API (local dev/testing)
    Program.cs                 # CLI with --endpoint/--model/--voice/--instructions options
    ConsoleVoiceLiveDemo.csproj # .NET 10, Azure.AI.VoiceLive, NAudio
  speech-tool/                 # CLI tool for testing TTS (standard voices)
    Program.cs                 # tts/voices commands (Speech SDK)
    SpeechTool.csproj          # .NET 8, Speech SDK, Azure.Identity
scripts/
  provision-phone-number.ps1  # Idempotent phone provisioning via ACS REST API (HMAC-SHA256 auth)
  setup-oidc.ps1              # OIDC setup for GitHub Actions
infra/
  main.bicep                  # Subscription-scoped entry point
  main.parameters.json        # azd parameters
  resources.bicep             # All Azure resources
```

---

## CI/CD Pipeline

3-job pipeline in `.github/workflows/deploy.yml`:

```
infra → build (2 parallel runners: admin-chat + caller-agent) → deploy
```

- **Auth:** Passwordless OIDC (federated credentials) — no secrets stored
- **Triggers:** Push to `main` on `code/**`, `infra/**`, `scripts/**`, `azure.yaml`, or `deploy.yml`; plus manual `workflow_dispatch`
- **Infra job:** Always logs into Azure; checks if provisioning is needed by detecting:
  - `infra/`, `azure.yaml`, or `scripts/` file changes
  - Missing ACR (fresh deploy)
  - Missing phone number on caller agent
- **Build job:** Waits for infra (ensures ACR exists); parallel matrix builds Docker images
- **Deploy job:** Updates both Container Apps in parallel
- **Version:** `1.0.0.<run_number>`

---

## Development Rules

- **Local-first workflow:** Always run and test changes locally before deploying to Azure.
- **Local `azd up` is allowed** — use it freely for fast iteration. The GitHub Action is still the canonical CI/CD path for `main`.

### Build & Deploy Rules

- **NEVER build Docker images locally.** The dev machine is **Snapdragon (ARM64)** — local `docker build` produces wrong-arch images that won't run on Azure Container Apps (linux/amd64).
- **Always use the remote builder:**
  - Preferred: `azd deploy <service>` (uses ACR Tasks under the hood)
  - Fallback: `az acr build --registry crcontactcenteragent --image <repo>:<tag> --file Dockerfile <build-args> .`
- For local code-only validation (no Docker), use `npm run build` in `code/admin-chat/` or `dotnet build` in the agent folder.
- **Required azd version: ≥ 1.24.1.** azd `1.23.6` has a known bug where it polls an ACR build-log blob before it exists, errors with `BlobNotFound 404`, and aborts — even though the ACR build itself succeeded and the image was pushed.
- **Recovery if `azd deploy` fails with `BlobNotFound`:** the image is almost certainly already in ACR. Verify and bump the Container App manually:
  ```pwsh
  az acr task list-runs -r crcontactcenteragent --top 5 -o table
  # Find the most recent Succeeded run + its image tag (e.g. azd-deploy-1777029098), then:
  az containerapp update -n ca-admin-chat -g rg-contactcenteragent `
    --image crcontactcenteragent.azurecr.io/contactcenteragent/admin-chat-demo:<tag>
  ```

### Services

| Service            | Status     | Runtime | Purpose                                                                                    |
| ------------------ | ---------- | ------- | ------------------------------------------------------------------------------------------ |
| Admin Chat Agent   | Deployed   | .NET 10 | Admin chat UI + .NET agent (Vue 3/Nuxt 3 + AG-UI + MS Agent Framework)                     |
| Caller Agent       | Deployed   | .NET 8  | Outbound AI phone calls (ACS + Voice Live API 2025-10-01 gpt-realtime, configurable voice) |
| Console Voice Demo | Local only | .NET 10 | Voice Live API demo CLI (NAudio mic input, Azure.AI.VoiceLive SDK)                         |
| Agent Apps         | Planned    | TBD     | Standalone agent apps (e.g., Weather Agent) deployed to ACA                                |

### Local Development

#### Admin Chat Agent (port 3000 + 8000)

```bash
cd code/admin-chat
npm install          # installs npm + dotnet dependencies
npm run dev          # starts Nuxt 3 (port 3000) + .NET agent (port 8000)
```

Configure the AI model via dotnet user-secrets:

```bash
cd code/admin-chat/agent
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://cog-contactcenteragent.cognitiveservices.azure.com/"
dotnet user-secrets set "CallerAgent:Url" "http://localhost:5000"   # point to local caller agent
# OR for free local dev:
dotnet user-secrets set "GitHubToken" "$(gh auth token)"
```

#### Caller Agent (port 5000)

Requires Azure Communication Services + Azure OpenAI credentials:

```bash
cd code/caller-agent/agent

# Required secrets:
dotnet user-secrets set "AcsConnectionString" "endpoint=https://acs-contactcenteragent.unitedstates.communication.azure.com/;accesskey=..."
dotnet user-secrets set "AcsPhoneNumber" "<your-acs-phone-number>"
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://cog-contactcenteragent.cognitiveservices.azure.com/"

# Voice configuration (defaults to OpenAI voice in appsettings.json):
# Switch voice / type:
dotnet user-secrets set "Voice:Type" "openai"
dotnet user-secrets set "Voice:Name" "alloy"
```

**Dev Tunnel** — ACS requires a public HTTPS URL for call callbacks. Use Azure dev tunnels:

```bash
# One-time setup (if not already created):
devtunnel create --allow-anonymous
devtunnel port create -p 5000

# Get the tunnel URL:
devtunnel show
# e.g. https://<your-subdomain>-5000.<region>.devtunnels.ms

# Start the tunnel (background):
devtunnel host &

# Start the caller agent with tunnel URL:
cd code/caller-agent/agent
$env:VS_TUNNEL_URL = "https://<your-subdomain>-5000.<region>.devtunnels.ms"
$env:ASPNETCORE_ENVIRONMENT = "Development"   # REQUIRED for user-secrets to load
dotnet run --urls "http://localhost:5000"
```

> **CRITICAL**: Always set `ASPNETCORE_ENVIRONMENT=Development` when running the caller agent locally. Without it, `dotnet user-secrets` will NOT be loaded and the app will fail with missing configuration errors.

---

## Real-Time Conversation Analysis

During live phone calls, the caller agent feeds transcripts to GPT-5.4-nano via structured outputs to produce real-time sentiment/intent scores. The analysis data flows:

```
AzureVoiceLiveService (transcript events)
  → ConversationAnalysisService.AddTranscriptAndAnalyzeAsync()
  → GPT-5.4-nano structured output (ChatResponseFormat.CreateJsonSchemaFormat, strict: true)
  → ChannelWriter<AnalysisResult>
  → CallerAgent SSE endpoint (GET /api/analysis/{contextId})
  → Nuxt proxy (server/api/analysis/[contextId].get.ts)
  → useCallAnalysis.ts composable (SSE fetch + ReadableStream)
  → AnalysisPanel.vue (15 horizontal bar gauges)
```

### 15 Analysis Categories (scored 0–5)

| Category             | Key (PascalCase)    | Inverted? |
| -------------------- | ------------------- | --------- |
| Purchase Intent      | PurchaseIntent      | No        |
| Customer Mood        | CustomerMood        | No        |
| Cooperativeness      | Cooperativeness     | No        |
| Brand Perception     | BrandPerception     | No        |
| Company Satisfaction | CompanySatisfaction | No        |
| Urgency              | Urgency             | No        |
| Engagement           | Engagement          | No        |
| Frustration          | Frustration         | Yes       |
| Trust in Agent       | TrustInAgent        | No        |
| Churn Risk           | ChurnRisk           | Yes       |
| Upsell Opportunity   | UpsellOpportunity   | No        |
| Resolution Progress  | ResolutionProgress  | No        |
| Politeness           | Politeness          | No        |
| Call Effectiveness   | CallEffectiveness   | No        |
| Overall Sentiment    | OverallSentiment    | No        |

### Analysis Architecture Details

- **ConversationAnalysisService.cs**: Accumulates full transcript, uses `SemaphoreSlim` to debounce (only one analysis in-flight at a time). Fire-and-forget `_ = Task.Run(() => RunAnalysisAsync())` after each transcript addition.
- **Schema**: JSON Schema with `additionalProperties: false` and `strict: true` — GPT-5.4-nano returns exactly the 15 integer fields.
- **Config**: `AzureOpenAI:AnalysisDeploymentName` in appsettings.json (set to `gpt-5.4-nano`).
- **Auth**: Uses `AzureOpenAIClient` with `DefaultAzureCredential` (same as other Azure OpenAI calls).
- **SSE format**: Each event is `data: {"PurchaseIntent":3,...,"Timestamp":"..."}\n\n`, terminated by `data: [DONE]\n\n` on call end.

### UI Layout (3-column)

```
┌──────────────────┬──────────────────┬──────────────────┐
│  Analysis Panel  │   Chat Panel     │  Transcription   │
│  (left sidebar)  │   (center)       │  (right sidebar) │
│  15 score bars   │   AG-UI chat     │  Live transcript  │
│  auto-opens on   │   with octopus   │  auto-opens on   │
│  call start      │   mascot         │  call start      │
└──────────────────┴──────────────────┴──────────────────┘
```

- Analysis panel toggle button: bottom-left of chat area
- Call log toggle button: bottom-left, above analysis toggle (always visible, with unread badge)
- Both sidebars auto-connect SSE when a call starts (contextId extracted from chat)
- ActivityPanel is hidden with `v-if="false"` (debug-only)

---

## Inbound Calls

The platform answers inbound phone calls on the ACS phone number, connects them to the Voice Live AI agent, and streams live transcription + analysis to the admin portal.

### Inbound Call Architecture

```
Caller dials ACS Phone Number (<your-acs-phone-number>)
  → EventGrid System Topic (evgt-contactcenteragent-acs)
  → EventGrid Subscription (IncomingCall filter)
  → Caller Agent webhook (POST /api/incomingCall)
  → AnswerCallAsync (ACS Call Automation)
  → WebSocket media streaming ↔ Voice Live API (gpt-realtime)
  → Transcription + Analysis channels
  → SSE endpoints → Admin Chat UI (TranscriptionPanel + AnalysisPanel)
```

### Key Implementation Details

- **EventGrid**: Subscription `incoming-call-to-caller-agent` filters `Microsoft.Communication.IncomingCall` events to `POST /api/incomingCall` on the caller agent.
- **Answer flow**: Same media streaming + Voice Live setup as outbound calls — bidirectional audio via WebSocket, PCM 24kHz mono.
- **UI integration**: Inbound calls create the same transcription/analysis channels as outbound calls, so the admin portal shows live transcription and analysis automatically.
- **Caller ID**: Extracted from the EventGrid event payload via `Helper.GetCallerId()` for telemetry correlation.

---

## Call Log

The platform tracks all inbound and outbound phone calls in a live call log panel in the admin portal.

### Call Log Architecture

```
Caller Agent (outbound call initiated / inbound call answered / call disconnected)
  → Broadcast CallLogEntry to SSE subscriber channels
  → Caller Agent SSE endpoint (GET /api/calls/stream)
  → Nuxt proxy (server/api/calls/stream.get.ts)
  → useCallLog.ts composable (SSE fetch + ReadableStream)
  → CallLogPanel.vue (sidebar)
```

### Key Implementation Details

- **Broadcast pattern**: Each SSE client gets its own `Channel<CallLogEntry>`. On call events (outbound initiated, inbound answered, call disconnected), the entry is written to all subscriber channels + `callLogHistory` (ConcurrentBag).
- **History replay**: When a new SSE client connects, all entries from `callLogHistory` are sent first (ordered by timestamp), then live events stream in.
- **Auto-connect**: The call log SSE stream connects automatically on page load via `onMounted(() => connectCallLog())`.
- **Unread badge**: The call log toggle button shows an unread count badge. Cleared when the panel is opened.
- **SSE format**: Each event is `data: {"Direction":"inbound","PhoneNumber":"+1...","Status":"connected","ContextId":"...","Name":null,"Purpose":null,"Timestamp":"..."}

`.

- **Call directions**: `outbound` (agent initiated), `inbound` (someone called in), `ended` (call disconnected).

---

## Authentication

Both agents use `DefaultAzureCredential` for Azure OpenAI access:

- **In Azure**: Managed Identity (auto-detected)
- **Locally**: Azure CLI credential (`az login`)

App Insights telemetry uses the `APPLICATIONINSIGHTS_CONNECTION_STRING` env var (injected by Bicep). The SDK auto-detects it — no credential or explicit connection string code needed.

> **IMPORTANT**: The admin-chat backend conditionally enables `UseAzureMonitor()` only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present. Without this guard, the app crashes on startup locally (no App Insights configured). See `AdminChatAgent.cs`.

---

## Social Sharing & Favicon

- **OG image**: `public/og-image.png` (1200×630, resized from `assets/icons/octopuswide.png`)
- **Favicon**: Official Norlys "Brand O" favicon from norlys.design (CVI package):
  - `public/favicon.ico` — multi-size ICO (15 KB)
  - `public/favicon-16x16.png` — 16×16 PNG
  - `public/favicon-32x32.png` — 32×32 PNG
  - `public/apple-touch-icon.png` — 180×180 Apple touch icon
  - `public/android-chrome-192x192.png` — 192×192 Android/PWA
  - `public/android-chrome-512x512.png` — 512×512 Android/PWA
- **Do NOT** replace these with custom icons — they are the official CVI favicon set.
- **Absolute URLs**: `og:image` and `twitter:image` require absolute URLs. The site URL is injected at Docker build time via the `NUXT_PUBLIC_SITE_URL` build arg (set in `deploy.yml`). Locally it falls back to a relative path.
- **Source assets**: `assets/icons/octopus.png` (1024×1024) and `assets/icons/octopuswide.png` (1536×1024) are the source files. Regenerate `public/` assets from these if changed.

---

## Telemetry & App Insights

All three layers log to a shared Application Insights resource (`appi-contactcenteragent`).

| Layer               | SDK                                          | Where events land                         | Custom dimensions                                                      |
| ------------------- | -------------------------------------------- | ----------------------------------------- | ---------------------------------------------------------------------- |
| admin-chat backend  | OpenTelemetry spans                          | `dependencies` table                      | `chat.event_type`, `chat.content`                                      |
| admin-chat frontend | `@microsoft/applicationinsights-web` browser | `exceptions`, `pageViews`, `dependencies` | `component`, `vueInfo`, `contextId`                                    |
| caller agent        | `TelemetryClient.TrackEvent/TrackException`  | `customEvents`, `exceptions`              | `chat.event_type`, `chat.content`, `chat.channel`, `chat.phone_number` |

### Frontend Telemetry (`plugins/appinsights.client.ts`)

- **Auto-tracked**: page views (SPA route changes), fetch/XHR dependencies, unhandled promise rejections
- **Vue errors**: captured via `nuxtApp.hook("vue:error", ...)` → `trackException()`
- **Manual**: `$appInsights?.trackException()` in `useAgentChat.ts` and `useCallTranscription.ts`
- **Cloud role**: `admin-chat-frontend` (set via telemetry initializer on `ai.cloud.role`)
- **Config**: connection string injected via `NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING` env var (Bicep)
- **Graceful fallback**: returns `null` when no connection string (local dev) — composables use `$appInsights?.` optional chaining

### Backend Exception Tracking

All `catch` blocks in the caller agent call `TelemetryClient.TrackException()` with a `Component` dimension:

- `CallerAgent.cs`: WebSocket handler, hang-up callback
- `AcsMediaStreamingHandler.cs`: `ProcessWebSocketAsync`, `StartReceivingFromAcsMediaWebSocket`
- `AzureVoiceLiveService.cs`: `InitializeAsync`, `ReceiveMessagesAsync`, `Close`

### KQL queries (App Insights → Logs)

```kql
// All user & AI messages from admin-chat (text chat)
dependencies
| where name in ("UserMessage", "AiMessage")
| extend eventType = tostring(customDimensions["chat.event_type"]),
         content   = tostring(customDimensions["chat.content"])
| project timestamp, eventType, content
| order by timestamp desc

// All user & AI messages from caller agent (voice calls)
customEvents
| where name in ("UserMessage", "AiMessage")
| extend eventType  = tostring(customDimensions["chat.event_type"]),
         content    = tostring(customDimensions["chat.content"]),
         channel    = tostring(customDimensions["chat.channel"]),
         phone      = tostring(customDimensions["chat.phone_number"])
| project timestamp, eventType, content, channel, phone
| order by timestamp desc

// Combined view — both agents, last 24h
let textChat = dependencies
| where name in ("UserMessage", "AiMessage")
| extend eventType = tostring(customDimensions["chat.event_type"]),
         content   = tostring(customDimensions["chat.content"]),
         channel   = "text",
         agent     = "admin-chat";
let voiceChat = customEvents
| where name in ("UserMessage", "AiMessage")
| extend eventType = tostring(customDimensions["chat.event_type"]),
         content   = tostring(customDimensions["chat.content"]),
         channel   = tostring(customDimensions["chat.channel"]),
         phone     = tostring(customDimensions["chat.phone_number"]),
         agent     = "caller";
union textChat, voiceChat
| where timestamp > ago(24h)
| project timestamp, agent, channel, eventType, content, phone
| order by timestamp desc

// Message counts per hour (monitoring dashboard)
let textChat = dependencies | where name in ("UserMessage", "AiMessage") | extend agent = "admin-chat";
let voiceChat = customEvents | where name in ("UserMessage", "AiMessage") | extend agent = "caller";
union textChat, voiceChat
| summarize count() by bin(timestamp, 1h), name, agent
| render timechart
```

### Azure CLI — query from terminal

```bash
# Set these once (or read from azd env)
APP_ID=$(az monitor app-insights component show -g rg-contactcenteragent -a appi-contactcenteragent --query appId -o tsv)

# Recent user & AI messages (admin-chat, text)
az monitor app-insights query -a $APP_ID --analytics-query "
  dependencies
  | where name in ('UserMessage','AiMessage')
  | extend eventType=tostring(customDimensions['chat.event_type']),
           content=tostring(customDimensions['chat.content'])
  | project timestamp, eventType, content
  | order by timestamp desc
  | take 20
"

# Recent user & AI messages (caller agent, voice)
az monitor app-insights query -a $APP_ID --analytics-query "
  customEvents
  | where name in ('UserMessage','AiMessage')
  | project timestamp, name,
      tostring(customDimensions['chat.content']),
      tostring(customDimensions['chat.channel']),
      tostring(customDimensions['chat.phone_number'])
  | order by timestamp desc
  | take 20
"
```

---

## Norlys Branding (admin-chat frontend)

Norlys has granted permission to use their CVI for this project. The admin-chat
UI follows the Norlys 2025 design system. Read these guidelines **before**
editing anything visual in `code/admin-chat/`.

### Reference links (Norlys Design 2025)

| Topic                       | URL                                                                          |
| --------------------------- | ---------------------------------------------------------------------------- |
| Hub                         | https://norlys.design/                                                       |
| Visuel identitet (overview) | https://norlys.design/document/307                                           |
| Logo                        | https://norlys.design/document/307#/grundelementer/logo                      |
| Farver (colors)             | https://norlys.design/document/307#/grundelementer/farver                    |
| Typografi                   | https://norlys.design/document/307#/grundelementer/typografi                 |
| Ikonografi                  | https://norlys.design/document/307#/grundelementer/ikonografi                |
| Digital design              | https://norlys.design/document/332                                           |
| Sidestruktur og layout      | https://norlys.design/document/332#/indhold-og-layout/sidestruktur-og-layout |
| Content blokke              | https://norlys.design/document/332#/indhold-og-layout/content-blokke         |

### Brand assets in this repo

- Source `.ai`, `.svg`, `.png` logo files: [For_web/](For_web/)
- Logos served by Nuxt (already deployed):
  - [code/admin-chat/public/norlys-logo.svg](code/admin-chat/public/norlys-logo.svg) — Norlys Red on light surfaces (use on Sand, Light Petroleum, white)
  - [code/admin-chat/public/norlys-logo-neg.svg](code/admin-chat/public/norlys-logo-neg.svg) — negative/Sand on dark surfaces (use on Norlys Red, Dark Petroleum, dark imagery)

### Color tokens (registered in [tailwind.config.ts](code/admin-chat/tailwind.config.ts))

Use the `norlys-*` Tailwind tokens — never hardcode hex.

| Token                    | Hex                   | Use                                                   |
| ------------------------ | --------------------- | ----------------------------------------------------- |
| `norlys-red`             | `#ed0812`             | Primary brand. Logo, primary CTA, splash, focus ring  |
| `norlys-red-2`           | `#d80812`             | Hover/active darker red                               |
| `norlys-red-3`           | `#c10000`             | Pressed state, high-contrast red                      |
| `norlys-petroleum`       | `#0c4c4e`             | Headlines on light, secondary buttons, icons          |
| `norlys-petroleum-2`     | `#004547`             | Hover petroleum                                       |
| `norlys-petroleum-3`     | `#023a3c`             | Strongest text/headlines on light                     |
| `norlys-light-petroleum` | `#dbe7e4`             | Borders, dividers, disabled surfaces                  |
| `norlys-sand`            | `#f4f2ec`             | **Default page background**                           |
| `norlys-sand-2/3`        | `#ece9e0` / `#e0dbcd` | Alt surfaces, code blocks                             |
| `norlys-ink` (Warm Grey) | `#413f3c`             | Body text on Sand / Light Petroleum / light imagery   |
| white (`#ffffff`)        | —                     | Body text on Norlys Red, Dark Petroleum, dark imagery |

**Text-color rule (CVI):** Warm Grey for text on light surfaces; Sand (or white in digital) for text on Norlys Red / Dark Petroleum.

**Primary color usage rule:** Norlys Red must be present in everything we
make — but never dominate. Use it on the logo, primary CTAs, focus rings, and
small accents. Avoid red backgrounds on large content areas.

### Typography

Configured in [tailwind.config.ts](code/admin-chat/tailwind.config.ts) and applied globally via [assets/css/main.css](code/admin-chat/assets/css/main.css).

| Use       | Tailwind class  | Stack                                                    | CVI rule                                    |
| --------- | --------------- | -------------------------------------------------------- | ------------------------------------------- |
| Headlines | `font-headline` | `"Norlys Headline", Georgia, "Times New Roman", serif`   | **Bold only**, **always left-aligned**      |
| Body      | `font-body`     | `"Norlys Text", Arial, Helvetica, system-ui, sans-serif` | Regular default; SemiBold/Bold for emphasis |

The proprietary `NORLYSHeadline-Bold.otf` and `NORLYSText-*.otf` files are gated behind login on norlys.design. The stack falls back to Norlys' own published web fallbacks (Georgia for headlines, Arial for body). If licensed `.otf` files are added later, drop them in `code/admin-chat/public/fonts/` and add `@font-face` blocks in `main.css`.

#### Form & input typography (CVI compliance)

The Norlys CVI defines exactly two typefaces — Norlys Headline (serif, bold-only, headlines) and Norlys Text (sans-serif, body). **There is no monospace font in the brand.** Therefore:

- **Never use `font-mono`** on inputs, textareas, phone numbers, code-like content, or any production UI. The only exception is the hidden debug `ActivityPanel` (`v-if="false"`).
- All `<input>`, `<textarea>`, and `<select>` elements inherit `font-body` from `<body>` — do not override.
- For aligned digits (phone numbers, scores, counts, timestamps), use **`tabular-nums`** instead of `font-mono`. This keeps the brand sans-serif while making digits the same width.
- Numeric labels and badges use `tabular-nums` plus the normal body weight.

### Logo placement (CVI)

- Norlys logo always sits in the **top-right corner** OR **bottom-left corner** of a layout.
- Respect distance: ≥ 1× brand "O" on small formats (≤ A4), 1.5× on larger.
- Logo width incl. respect distance must never exceed **50%** of the format width.
- The negative (Sand) logo is for Norlys Red and Dark Petroleum backgrounds; the positive (Red) logo is for Sand, Light Petroleum, and light imagery.

### Digital design principles (https://norlys.design/document/332)

These apply to all admin-chat pages and any future agent UIs:

1. **Mobile-first responsive.** Design and test small screens first; scale up.
2. **Synligt næste modul.** The top of the next section must be visible above the fold to invite scrolling.
3. **Less is more.** Avoid dense text/components; use white space for visual rhythm.
4. **Konvertering & SEO.** Every page needs an obvious CTA and search-engine-friendly markup (semantic HTML, alt text, meta tags).
5. **Content blocks** (image + headline + 1–2 line body + CTA) tell one story and lead to one action.
6. **Headlines:** short, max 2 lines, signal the block's message clearly.
7. **CTAs:** action-oriented, mirror the headline's message ("Bestil elaftale", "Se priser og bestil"). Never generic "Klik her".
8. **Bullet lists** for scannable info; **bold** for keywords; _italic_ for clarifications — both used sparingly.
9. **Splash elements** are reserved for price, savings, or a clear advantage. Don't dilute them with general use.
10. **Mega menu:** max 7 top categories; max 3 sub-headings × 5 links each.

### Accessibility (WCAG, mandatory from June 2025)

The Norlys CVI explicitly commits to WCAG. When adding UI:

- Maintain ≥ 4.5:1 contrast for body text and ≥ 3:1 for large text/icons. Norlys Red on Sand passes; Norlys Red on white passes for text ≥ 18pt; verify any combination with a contrast checker before shipping.
- All interactive elements need a visible focus ring — use the `norlys-red/20` ring already wired into the chat input as the pattern.
- All images need `alt` text; decorative images use `alt=""`.
- All buttons need an accessible name (visible text or `aria-label`).
- Form fields need real `<label>` elements (not just placeholders).
- Don't convey state with color alone; pair with text or an icon.
- Respect `prefers-reduced-motion` for animations.

### Tone of voice (Danish UI)

- Enkelhed, handlekraft, optimisme. Korte sætninger, aktivt sprog.
- Body copy in Danish. Code, identifiers, and English-language errors stay in English.

### Azure OpenAI SDK Versions

- **caller-agent** uses `Azure.AI.OpenAI` **v2.1.0** (.NET 8)
- **admin-chat** uses `Azure.AI.OpenAI` **v2.8.0-beta.1** (.NET 10)
- These versions have different API surfaces. Code that works in one may not compile in the other.
- `ChatResponseFormat.CreateJsonSchemaFormat()` works in both, but parameter names/overloads differ.

### GPT-5.4-nano — Do NOT Use `MaxOutputTokenCount` with Azure.AI.OpenAI v2.1.0

- `Azure.AI.OpenAI` v2.1.0 sends `MaxOutputTokenCount` as `max_tokens` in the HTTP request.
- GPT-5.4-nano **rejects `max_tokens`** with `400 BadRequest: Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.`
- **Fix**: Do NOT set `MaxOutputTokenCount` on `ChatCompletionOptions` when using the v2.1.0 SDK with GPT-5.4-nano. Omit it entirely and let the model use its default.
- This was the root cause of the analysis panel showing no data — the error was silently swallowed by the fire-and-forget pattern.
- The newer `Azure.AI.OpenAI` v2.8.0-beta.1 (used by admin-chat) correctly sends `max_completion_tokens` instead.

### GPT-5.4-nano — Do NOT Use `ReasoningEffort`

- Setting `ChatCompletionOptions.ReasoningEffort = ChatReasoningEffort.Low` (or any reasoning options) causes a **400 BadRequest** from Azure OpenAI with error: `Unknown parameter: 'reasoning'`.
- GPT-5.4-nano on Azure AI Foundry (AIServices kind) does **not** support the reasoning parameter.
- **Fix**: Remove any `ConfigureOptions` block that sets `ReasoningEffort` or `ReasoningOptions`.

### Silent Failures in Fire-and-Forget Patterns

- `ConversationAnalysisService` uses `_ = Task.Run(() => RunAnalysisAsync())` which **silently swallows all exceptions**.
- The logger in `ConversationAnalysisService` was originally created with bare `new LoggerFactory()` (no providers) — all log output goes to `/dev/null`.
- **When analysis doesn't work**: Add `Console.WriteLine` in `RunAnalysisAsync` catch blocks, or pass a real `ILoggerFactory` with console provider.
- Always prefer `LoggerFactory.Create(builder => builder.AddConsole())` over bare `new LoggerFactory()` for services created outside DI.

### `ASPNETCORE_ENVIRONMENT` Must Be Set for User-Secrets

- .NET user-secrets only load when `ASPNETCORE_ENVIRONMENT=Development`.
- When running the caller agent manually with `dotnet run`, always set this env var **before** starting.
- Without it, `AcsConnectionString`, `AcsPhoneNumber`, and `AzureOpenAI:Endpoint` will be null/empty and the app will fail silently or crash.

### `UseAzureMonitor()` Crashes Without App Insights

- `builder.Services.AddOpenTelemetry().UseAzureMonitor()` throws if `APPLICATIONINSIGHTS_CONNECTION_STRING` is not set.
- The admin-chat backend wraps this in a conditional check. **Do not remove** that guard.

### ACS Callback URL Requires Public HTTPS

- Azure Communication Services call automation callbacks require a publicly reachable HTTPS URL.
- Locally, use `devtunnel` to expose port 5000. Set `VS_TUNNEL_URL` env var.
- The caller agent uses `VS_TUNNEL_URL` (if set) as the callback base URL, falling back to the request's own host.

### SSE Proxy Pattern (Nuxt → .NET)

- Both transcription and analysis use the same SSE proxy pattern:
  1. .NET backend writes to `Channel<T>` → SSE endpoint reads and streams
  2. Nuxt server route proxies the SSE stream (handles abort on client disconnect)
  3. Vue composable reads via `fetch()` + `ReadableStream` (not `EventSource`, which can't set custom headers)
- The channel is created when a call starts and completed (`TryComplete()`) on `CallDisconnected`.
- **If the channel is completed before the SSE client connects**, the client gets `[DONE]` immediately with no data.

### Per-Call State Management (Caller Agent)

- All per-call state (prompts, languages, phone numbers, channels) is stored in `ConcurrentDictionary` keyed by `contextId`.
- `contextId` comes from `CreateCallOptions.OperationContext` or `AnswerCallOptions.OperationContext`.
- On `CallDisconnected`, ALL per-call dictionaries are cleaned up. Missing cleanup causes memory leaks.
- Both `transcriptionChannels` and `analysisChannels` must be created together and cleaned up together.

### Structured Output JSON Schema Requirements

- Azure OpenAI structured outputs with `strict: true` require `additionalProperties: false` on the schema.
- All properties must be listed in the `required` array.
- The schema `name` must be alphanumeric with underscores only (no spaces or special chars).
- Max completion tokens should be generous enough for the full JSON (100–200 tokens for 15 integer fields).

### Caller Agent Phone Number

- ACS phone number: `<your-acs-phone-number>` (local US)
- Stored in user-secrets as `AcsPhoneNumber` and in appsettings as `AcsPhoneNumber`.
- The `provision-phone-number.ps1` script handles idempotent provisioning.

### Dev Tunnel URL

- Current tunnel: `https://<your-subdomain>-5000.<region>.devtunnels.ms` (maps to localhost:5000)
- Must be running (`devtunnel host`) before placing calls locally.
- The tunnel URL is set via `$env:VS_TUNNEL_URL` when starting the caller agent.

Important design guidelines: https://norlys.design

https://norlys.design/document/307#/grundelementer/logo/download-logo
Visuel identitet - Norlys Design 2025
A Powerful Brand Powered by Frontify

https://norlys.design/document/307#/grundelementer/typografi
Visuel identitet - Norlys Design 2025
A Powerful Brand Powered by Frontify

https://norlys.design/document/307#/grundelementer/farver
Visuel identitet - Norlys Design 2025
A Powerful Brand Powered by Frontify

ikon https://norlys.design/document/295
