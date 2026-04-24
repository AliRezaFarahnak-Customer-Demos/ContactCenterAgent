# App Architecture & Dependencies

Detailed architecture reference for the Contact Center Agent platform.
For quick-start, branding, and dev rules → see `copilot-instructions.md`.

## Reference Documentation

| Topic                   | URL                                                                                                      |
| ----------------------- | -------------------------------------------------------------------------------------------------------- |
| Agent Framework docs    | https://learn.microsoft.com/en-us/agent-framework/                                                       |
| Agent Framework GitHub  | https://github.com/microsoft/agent-framework                                                             |
| Agent Framework samples | https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted                     |
| Agent Framework NuGet   | https://www.nuget.org/profiles/MicrosoftAgentFramework                                                   |
| AG-UI protocol          | https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/?pivots=programming-language-python |
| AG-UI frontend tools    | https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/frontend-tools                      |
| AG-UI backend rendering | https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/backend-tool-rendering              |
| AG-UI SDK docs          | https://docs.ag-ui.com/sdk/python/core/overview                                                          |
| AG-UI GitHub            | https://github.com/ag-ui-protocol/ag-ui                                                                  |
| @aivue/chatbot          | https://www.npmjs.com/package/@aivue/chatbot                                                             |
| @aivue/analytics        | https://www.npmjs.com/package/@aivue/analytics                                                           |

---

## Solution Architecture

| Layer      | Technology                                                   |
| ---------- | ------------------------------------------------------------ |
| Runtime    | Azure Container Apps (.NET 10 backend + Nuxt 3 Vue frontend) |
| AI Backend | Azure AI Foundry (`AIServices` kind, S0) — GPT-5.4-nano      |
| Frontend   | Vue 3 + Nuxt 3 (AG-UI protocol over SSE, SPA mode)           |
| Storage    | Azure Storage Account (Table Storage)                        |
| IaC        | Bicep (subscription-scoped `main.bicep` → `resources.bicep`) |
| Region     | `swedencentral`                                              |

### Azure Resources

| Resource            | Name                          | Notes                                    |
| ------------------- | ----------------------------- | ---------------------------------------- |
| Resource Group      | `rg-contactcenteragent`       | All resources                            |
| AI Foundry          | `cog-contactcenteragent`      | AIServices S0, Managed Identity          |
| AI Foundry Project  | `cog-contactcenteragent-prj`  | Child of AI Foundry                      |
| Model: gpt-5.4-nano | `gpt-5.4-nano`                | GlobalStandard, capacity 1000            |
| Model: gpt-realtime | `gpt-realtime`                | gpt-realtime-1.5, GlobalStandard, cap 10 |
| Storage             | `stcontactcenteragent`        | Standard_LRS, Table Storage              |
| Container Registry  | `crcontactcenteragent`        | Standard SKU, admin enabled              |
| Log Analytics       | `log-contactcenteragent`      | PerGB2018, 30-day retention              |
| Container Apps Env  | `cae-contactcenteragent`      | Logs to Log Analytics                    |
| App Insights        | `appi-contactcenteragent`     | Shared telemetry                         |
| ACS                 | `acs-contactcenteragent`      | Phone numbers, call routing              |
| EventGrid Topic     | `evgt-contactcenteragent-acs` | Routes IncomingCall to caller agent      |
| Container App       | `ca-admin-chat`               | Admin Chat (.NET 10 + Nuxt 3)            |
| Container App       | `ca-caller-agent`             | Caller Agent (.NET 8, ACS + Realtime)    |

### Tenant & Secrets

- Tenant-specific values → `.azure/<env>/.env` (gitignored). Never commit.
- `azd up` provisions everything; `azd down` tears down.
- Auth: `DefaultAzureCredential` (Managed Identity in Azure, `az login` locally).

---

## CI/CD Pipeline

3-job pipeline: `infra → build (parallel matrix) → deploy`

- OIDC auth (no secrets stored), triggers on `main` push to `code/**`, `infra/**`, `scripts/**`
- Version: `1.0.0.<run_number>`

---

## Services

| Service            | Status     | Runtime | Port        |
| ------------------ | ---------- | ------- | ----------- |
| Admin Chat         | Deployed   | .NET 10 | 3000 + 8000 |
| Caller Agent       | Deployed   | .NET 8  | 5000        |
| Console Voice Demo | Local only | .NET 10 | —           |
| Speech Tool        | Local only | .NET 8  | —           |

---

## Local Dev Setup

### Admin Chat

```bash
cd code/admin-chat/agent
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://cog-contactcenteragent.cognitiveservices.azure.com/"
dotnet user-secrets set "CallerAgent:Url" "http://localhost:5000"
# OR for free local dev:
dotnet user-secrets set "GitHubToken" "$(gh auth token)"
```

### Caller Agent

```bash
cd code/caller-agent/agent
dotnet user-secrets set "AcsConnectionString" "endpoint=https://acs-contactcenteragent.unitedstates.communication.azure.com/;accesskey=..."
dotnet user-secrets set "AcsPhoneNumber" "<your-acs-phone-number>"
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://cog-contactcenteragent.cognitiveservices.azure.com/"
dotnet user-secrets set "Voice:Type" "openai"
dotnet user-secrets set "Voice:Name" "alloy"
```

### Dev Tunnel (required for ACS callbacks)

```bash
devtunnel create --allow-anonymous && devtunnel port create -p 5000
devtunnel host &
$env:VS_TUNNEL_URL = "https://<your-subdomain>-5000.<region>.devtunnels.ms"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --urls "http://localhost:5000"
```

### Build & Deploy

- **NEVER docker build locally** (ARM64 machine). Use `azd deploy` or `az acr build`.
- **azd ≥ 1.24.1** required. Recovery for BlobNotFound bug:
  ```pwsh
  az acr task list-runs -r crcontactcenteragent --top 5 -o table
  az containerapp update -n ca-admin-chat -g rg-contactcenteragent --image crcontactcenteragent.azurecr.io/contactcenteragent/admin-chat-demo:<tag>
  ```

---

## Data Flows

### SSE Proxy Pattern (Nuxt → .NET)

All real-time streams (transcription, analysis, call log) follow the same pattern:

1. .NET backend writes to `Channel<T>` → SSE endpoint streams
2. Nuxt server route proxies the SSE stream (handles abort on disconnect)
3. Vue composable reads via `fetch()` + `ReadableStream`

- Channel created on call start, completed (`TryComplete()`) on `CallDisconnected`

### Real-Time Conversation Analysis

```
VoiceLive transcript → ConversationAnalysisService → GPT-5.4-nano structured output
  → Channel<AnalysisResult> → SSE → Nuxt proxy → AnalysisPanel.vue (15 bar gauges)
```

**15 categories** (scored 0–5): PurchaseIntent, CustomerMood, Cooperativeness, BrandPerception, CompanySatisfaction, Urgency, Engagement, Frustration (inverted), TrustInAgent, ChurnRisk (inverted), UpsellOpportunity, ResolutionProgress, Politeness, CallEffectiveness, OverallSentiment.

- Uses `SemaphoreSlim` debounce (one analysis in-flight at a time)
- Schema: `strict: true`, `additionalProperties: false`
- Config: `AzureOpenAI:AnalysisDeploymentName` = `gpt-5.4-nano`

### Inbound Calls

```
Phone → EventGrid (IncomingCall) → POST /api/incomingCall → AnswerCallAsync
  → WebSocket ↔ Voice Live API → Transcription + Analysis channels → SSE → UI
```

### Call Log

```
Call events → broadcast to Channel<CallLogEntry> per SSE client
  → GET /api/calls/stream → Nuxt proxy → CallLogPanel.vue
```

- History replay on connect, unread badge, auto-connect on page load

### UI Layout (3-column)

```
┌────────────────┬────────────────┬────────────────┐
│ Analysis (15)  │  Chat (AG-UI)  │ Transcription  │
│ left sidebar   │  center        │ right sidebar  │
└────────────────┴────────────────┴────────────────┘
```

---

## Telemetry & App Insights

Shared resource: `appi-contactcenteragent`

| Layer    | SDK                                  | Table                     |
| -------- | ------------------------------------ | ------------------------- |
| Backend  | OpenTelemetry spans                  | `dependencies`            |
| Frontend | `@microsoft/applicationinsights-web` | `exceptions`, `pageViews` |
| Caller   | `TelemetryClient.TrackEvent`         | `customEvents`            |

### KQL Queries

```kql
// Text chat messages
dependencies | where name in ("UserMessage", "AiMessage")
| extend eventType = tostring(customDimensions["chat.event_type"]),
         content = tostring(customDimensions["chat.content"])
| project timestamp, eventType, content | order by timestamp desc

// Voice call messages
customEvents | where name in ("UserMessage", "AiMessage")
| extend content = tostring(customDimensions["chat.content"]),
         channel = tostring(customDimensions["chat.channel"]),
         phone = tostring(customDimensions["chat.phone_number"])
| project timestamp, content, channel, phone | order by timestamp desc

// Combined view (last 24h)
let text = dependencies | where name in ("UserMessage","AiMessage") | extend agent="admin-chat";
let voice = customEvents | where name in ("UserMessage","AiMessage") | extend agent="caller";
union text, voice | where timestamp > ago(24h) | order by timestamp desc
```

---

## Social Sharing & Favicon

- **OG image**: `public/og-image.png` (1200×630). Absolute URL via `NUXT_PUBLIC_SITE_URL` build arg.
- **Favicon**: Official Norlys "Brand O" set — do NOT replace (see copilot-instructions.md).

---

## Known Gotchas

### Azure OpenAI SDK Versions

- **caller-agent**: `Azure.AI.OpenAI` **v2.1.0** (.NET 8)
- **admin-chat**: `Azure.AI.OpenAI` **v2.8.0-beta.1** (.NET 10)
- Different API surfaces — code that compiles in one may not compile in the other.

### GPT-5.4-nano Restrictions

- **Do NOT set `MaxOutputTokenCount`** with v2.1.0 SDK — sends `max_tokens` which GPT-5.4-nano rejects. Omit entirely.
- **Do NOT set `ReasoningEffort`** — GPT-5.4-nano doesn't support the reasoning parameter.

### Silent Failures

- `ConversationAnalysisService` fire-and-forget (`_ = Task.Run(...)`) swallows exceptions.
- Use `LoggerFactory.Create(b => b.AddConsole())` not bare `new LoggerFactory()`.

### Environment Guards

- `ASPNETCORE_ENVIRONMENT=Development` required for user-secrets to load.
- `UseAzureMonitor()` crashes without `APPLICATIONINSIGHTS_CONNECTION_STRING` — keep the conditional guard.
- ACS callbacks need public HTTPS — use `devtunnel`, set `VS_TUNNEL_URL`.

### Per-Call State (Caller Agent)

- All per-call state in `ConcurrentDictionary` keyed by `contextId`.
- On `CallDisconnected`, clean up ALL dictionaries. Missing cleanup = memory leak.
- `transcriptionChannels` and `analysisChannels` must be created/cleaned together.

### Structured Output Schema

- `strict: true` requires `additionalProperties: false` + all props in `required`.
- Schema `name`: alphanumeric + underscores only.
