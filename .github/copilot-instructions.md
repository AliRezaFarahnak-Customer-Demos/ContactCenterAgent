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
| AI Backend | Azure AI Foundry (`AIServices` kind, S0) — GPT-5.4-nano             |
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
| Model Deployment   | `gpt-5.4-nano`                     | GPT-5.4-nano, GlobalStandard, capacity 1000                 |
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
- **Favicon**: `public/favicon.ico` (multi-size 16/32/48) + `public/favicon.png` (48×48), generated from `assets/icons/octopus.png`
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

## Known Pitfalls & Debugging Learnings

Hard-earned lessons from debugging sessions — **read before making changes**.

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

