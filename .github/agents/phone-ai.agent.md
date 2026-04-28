# Phone AI Agent — Technical Architecture & Knowledge Base

## Reference Documentation

Always consult these for up-to-date API usage, samples, and patterns:

| Topic                            | URL                                                                                                                                                   |
| -------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| Get a phone number (ACS)         | https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/telephony/get-phone-number?tabs=windows&pivots=programming-language-csharp |
| Phone Numbers sample (.NET)      | https://github.com/Azure-Samples/communication-services-dotnet-quickstarts/tree/main/PhoneNumbers                                                     |
| Outbound Call Reminder sample    | https://github.com/Azure-Samples/communication-services-dotnet-quickstarts/tree/main/OutboundCallReminder                                             |
| Call Automation + OpenAI sample  | https://github.com/Azure-Samples/communication-services-dotnet-quickstarts/tree/main/callautomation-openai-sample-csharp                              |
| ACS Phone Numbers REST API       | https://learn.microsoft.com/en-us/rest/api/communication/phonenumbers (only `list`/`show` exist in CLI — `search`/`purchase` require REST API)        |
| Azure OpenAI Realtime API models | https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/models — `gpt-realtime` GA (2025-08-28), available in Sweden Central              |

---

## Purpose

Enable the ContactCenterAgent admin dashboard to initiate outbound AI phone calls. An admin types a command like _"Call +4580719050 about their account"_ and the system places a real phone call where an AI agent has a live voice conversation with the human who answers.

---

## Architecture Overview

The caller agent is **fully integrated into the ContactCenterAgent monorepo** — there is no separate AIPhone repo. All code lives under `code/caller-agent/`, all infrastructure is provisioned by the shared Bicep templates in `infra/`, and the voice AI model (`gpt-realtime`) is deployed on the **same AI Foundry resource** (`cog-contactcenteragent`) used by the admin chat agent.

---

## End-to-End Call Flow

```
┌─────────────────┐     ┌──────────────────┐     ┌──────────────────────┐     ┌───────────┐
│  Admin Chat UI  │────▶│  ContactCenterAgent .NET   │────▶│  Caller Agent API    │────▶│   Azure   │
│  (Nuxt 3)       │     │  Agent (AG-UI)   │     │  (Container App)     │     │   ACS     │
│  localhost:3000  │     │  localhost:8000   │     │  ca-caller-agent     │     │           │
└─────────────────┘     └──────────────────┘     └──────────────────────┘     └─────┬─────┘
                                                                                    │
                                                                             Human's phone
                                                                               rings...
                                                                                    │
                                                                             Human answers
                                                                                    │
                                                                            ┌───────▼───────┐
                                                                            │  ACS WebSocket │
                                                                            │  Media Stream  │
                                                                            │       ↕        │
                                                                            │  Azure OpenAI  │
                                                                            │  Realtime API  │
                                                                            │ (gpt-realtime) │
                                                                            │  on cog-contactcenteragent│
                                                                            └────────────────┘
```

### Step-by-Step

1. **Admin types** in the chat UI: _"Call +4580719050 and ask about their subscription renewal"_
2. **ContactCenterAgent .NET agent** (`AdminChatAgent.cs`) recognizes intent → invokes `MakePhoneCall` tool with `countryCode`, `phoneNumber`, `talkAboutThis`, `name`, `language`, `languageCode`, and `transcriptionHint` parameters
3. **Tool calls** `POST https://ca-caller-agent.<region>.azurecontainerapps.io/api/outboundCall` with the phone number, purpose, name, optional language, languageCode, and transcriptionHint (no system prompt — the caller agent builds the prompt and always appends `CorePhoneRules`)
4. **Caller Agent API** generates a unique context ID, stores the per-call system prompt in a `ConcurrentDictionary`, force-refreshes the AI credential token (using `claims: "{}"` to bypass MSAL cache), and calls `client.CreateCallAsync()` via Azure Communication Services SDK
5. **ACS** places an outbound PSTN call from the provisioned phone number to the target
6. **Human answers** → ACS sends `CallConnected` event to the callback URL (`/api/callbacks/{contextId}`)
7. **ACS** opens a WebSocket connection to `/ws` with bidirectional PCM 24kHz mono audio
8. **AcsMediaStreamingHandler** receives the WebSocket, initializes **AzureVoiceLiveService** which connects to Azure OpenAI Realtime API via raw WebSocket using the **force-refreshed singleton `DefaultAzureCredential`** (ManagedIdentity in Azure, Azure CLI locally)
9. **AI speaks** with the human in real-time (full duplex, barge-in support, transcription)
10. **Call ends** → AI invokes `hang_up` tool → **two-phase hang-up**: tool output forces a farewell `response.create` → model speaks goodbye → on `response.done` + ~2s audio flush → `CallConnection.HangUpAsync(true)` disconnects → transcript and status returned to the admin agent → displayed in the chat UI

---

## Codebase Structure

All code is self-contained in the ContactCenterAgent repo — no external repo dependencies.

```
code/admin-chat/
  agent/
    AdminChatAgent.cs             # Admin chat agent (renamed from Program.cs) — tools, system prompt, MakePhoneCall
  pages/
    index.vue                     # Frontend — octopus mascot, "AI Call Center" heading, call suggestion button
  composables/
    useAgentChat.ts               # AG-UI SSE client composable + App Insights exception tracking
    useCallTranscription.ts       # Live call transcription via SSE + App Insights tracking
  components/
    ChatMessage.vue               # Chat message component (markdown + streaming)
    ActivityPanel.vue             # Debug/session activity sidebar
    TranscriptionPanel.vue        # Live call transcript sidebar (right panel, mobile slide-over)
  plugins/
    appinsights.client.ts         # App Insights browser SDK (auto page views, Vue errors, exceptions)
  server/
    api/
      agent.post.ts               # Proxy route → .NET agent
      transcription/
        [contextId].get.ts        # SSE proxy → caller agent transcription stream
  assets/icons/
    octopus.png                   # AI octopus mascot image (1024x1024, ~400KB)

code/caller-agent/
  Dockerfile                      # Multi-stage .NET 8 build + runtime
  agent/
    CallerAgent.cs                # API endpoints (renamed from Program.cs) — credential singleton, per-call token force-refresh, hang-up callback
    AcsMediaStreamingHandler.cs   # WebSocket ↔ ACS media stream bridge, forwards credential to AI service
    AzureVoiceLiveService.cs      # Azure OpenAI Realtime session via raw WebSocket (force-refreshed credential)
    Helper.cs                     # EventGrid parsing, caller ID extraction (SIP format handling)
    CallerAgent.csproj            # .NET 8 Web project with all SDK packages
    appsettings.json              # Config structure (AcsConnectionString, AzureOpenAI, AppInsights)
    appsettings.Development.json  # Dev overrides

scripts/
  provision-phone-number.ps1      # Idempotent phone provisioning via ACS REST API (HMAC-SHA256 auth)

infra/
  resources.bicep                 # ACS + Container App + gpt-realtime model deployment
  main.bicep                      # Subscription-scoped entry point (no separate AI endpoint param)
```

### Key Files and Their Roles

| File                          | Purpose                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `CallerAgent.cs`              | (Renamed from `Program.cs`) Minimal API with `POST /api/outboundCall`, `POST /api/incomingCall`, `POST /api/callbacks/{contextId}`, `GET /api/transcription/{contextId}` (SSE), WebSocket `/ws`. Creates singleton `TokenCredential`, pre-warms token at startup, force-refreshes before each call (outbound + inbound) using `claims: "{}"` to bypass MSAL cache. Per-call prompt storage via `ConcurrentDictionary` (prompts, languages, languageCodes, transcriptionHints, phoneNumbers). Transcription channels via `Channel<TranscriptionEvent>` streamed to SSE consumers. Hang-up callback wiring via `CallConnection.HangUpAsync(true)`. Accepts `Name`, `LanguageCode`, and `TranscriptionHint` parameters. All catch blocks call `TrackException`.                                                                                                                                                                                                                                                             |
| `AcsMediaStreamingHandler.cs` | Receives ACS WebSocket, parses `StreamingData`, forwards audio to AI, sends AI audio back. Accepts and forwards `TokenCredential` and `ChannelWriter<TranscriptionEvent>` to `AzureVoiceLiveService`. All catch blocks call `TrackException`.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `AzureVoiceLiveService.cs`    | Connects to Voice Live API via raw `ClientWebSocket` at `/voice-live/realtime?api-version=2025-10-01` (not the SDK). Uses injected singleton `TokenCredential` for auth. Defines `CorePhoneRules` const (always appended to any prompt). Configures `azure_semantic_vad_multilingual` (no EOU detection — only for cascaded pipelines), deep noise suppression, echo cancellation, OpenAI Alloy voice. Builds the transcription config via `BuildTranscriptionConfig(configuration, languageCode, transcriptionHint, logger)` — defaults to `azure-speech` (`language` = BCP-47 + Norlys `phrase_list`); falls back to `whisper-1` / `gpt-4o-transcribe` when `Transcription:Model` is overridden (those use `prompt` instead of `phrase_list`). Writes transcription events via `ChannelWriter.TryWrite()`. Implements **two-phase hang-up** (forced farewell → disconnect on `response.done`). Error handler continues on recoverable errors (only breaks on `session_error`). All catch blocks call `TrackException`. |
| `Helper.cs`                   | Parses EventGrid `BinaryData` → `JsonObject`, extracts caller ID (handles SIP `sip:+14155551234@sbc.example.com` format), validates incoming call context (JWT parts)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `CallerAgent.csproj`          | .NET 8 Web SDK project; all packages listed in SDK Packages section below                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| `appsettings.json`            | Nested config: `AzureOpenAI.Endpoint`, `AzureOpenAI.DeploymentName`, `AcsConnectionString`, `AcsPhoneNumber` (App Insights auto-detected via `APPLICATIONINSIGHTS_CONNECTION_STRING` env var — no config key needed)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| `provision-phone-number.ps1`  | Idempotent US toll-free number provisioning; uses ACS REST API v2024-03-01-preview with HMAC-SHA256 auth (Azure CLI only has `list`/`show`, NOT `search`/`purchase`)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |

---

## Credential & Token Management

### Singleton Pattern (avoids cold-start delay)

The AI credential is created **once** at app startup in `CallerAgent.cs` (renamed from `Program.cs`) and shared across all calls:

```csharp
var aiCredential = new DefaultAzureCredential();
```

`DefaultAzureCredential` auto-selects the right credential: **ManagedIdentity** in Azure Container Apps, **Azure CLI** locally. No environment-sniffing code needed.

### Token Refresh Strategy

1. **Startup pre-warm**: `GetTokenAsync()` is called during app boot so the first call never pays the ~1-3s token acquisition penalty (e.g., `az account get-access-token` for AzureCliCredential).
2. **Per-call force-refresh**: Both `/api/outboundCall` and `/api/incomingCall` call `GetTokenAsync(claims: "{}")` **before** placing/answering the call. The `claims` parameter forces MSAL to bypass its token cache and request a brand-new token from the identity provider. ACS takes several seconds to set up media streaming, so the fresh token (~1h lifetime) is guaranteed valid when `AzureVoiceLiveService.InitializeAsync` uses it.
3. **Thread-safe sharing**: `TokenCredential` is thread-safe; multiple parallel calls share the same cached token and the SDK deduplicates concurrent refresh requests.

### Dependency Injection Path

`CallerAgent.cs` (singleton) → `AcsMediaStreamingHandler` (constructor param) → `AzureVoiceLiveService` (constructor param `m_credential`) → `m_credential.GetTokenAsync()` during `InitializeAsync()`.

---

## System Prompt Architecture

### CorePhoneRules (always applied)

Extracted as a `const string` in `AzureVoiceLiveService.cs`, this block is **always appended** to any system prompt — whether the default or a per-call override:

```
PHONE RULES (always apply):
- Never admit you are an AI.
- Keep answers as short as possible — this is a phone call, not a text chat.
- Your opening greeting MUST be 3-4 words max, e.g. 'Hey, what's up?' — no long introductions.
- HANG-UP RULE: When the conversation is wrapping up (the user says 'fine', 'okay', 'bye', 'thanks', 'that's all', etc.), you MUST first say a brief friendly farewell and THEN call the hang_up tool. Never call hang_up without saying goodbye first.
```

### Default System Prompt

```
You are a helpful persona on a phone call.
```

- `CorePhoneRules` (always appended)

### Per-Call Prompt Composition

When the admin chat agent invokes `make_phone_call` with `purpose`, `name`, and optional `language`:

1. **Caller Agent `CallerAgent.cs`** builds: `"You are making an outbound phone call. You are calling {Name}. Your goal: {Purpose}."`
2. **`AzureVoiceLiveService` constructor** appends `CorePhoneRules` to the per-call prompt.
3. **`UpdateSessionAsync()`** appends a language instruction if `Language` is specified: _"You MUST speak ONLY in {language} for the ENTIRE call — greetings, answers, farewell, everything."_

The admin chat agent sends `phoneNumber`, `purpose`, `name`, `language`, `languageCode`, and `transcriptionHint` — no `systemPrompt` field. This ensures `CorePhoneRules` are never accidentally omitted.

### Multilingual Support

The voice (OpenAI `alloy`) supports multilingual output — it follows the language specified in the system prompt instructions. No voice swap is needed. The language rule is heavily reinforced in the system prompt to prevent confusion between similar-sounding languages (e.g., Danish vs English):

- **System prompt**: Includes explicit per-language instructions and strict anti-mixing rules.
- **Transcription language hint**: The `input_audio_transcription` config includes an ISO 639-1 `language` code so the transcription model correctly identifies the spoken language instead of guessing. This is critical for Danish/English disambiguation since accents can sound similar.
- **Transcription vocabulary bias**: The `input_audio_transcription` config includes vocabulary hints in the target language (e.g., Norlys-domain terms for a Danish invoice call). For the default `azure-speech` model these are sent as a `phrase_list` (BCP-47 `language`, e.g. `da-DK`); for `whisper-1` / `gpt-4o-transcribe` overrides they are sent as a free-text `prompt`. Hints are generated by GPT-5.4-nano at tool-call time, tailored to both the language and the call topic — never hardcoded.
- **Transcription model**: Defaults to **`azure-speech`** (Microsoft's flagship Danish ASR, BCP-47 `language` + `phrase_list`). Override with `Transcription:Model` config to use `whisper-1`, `gpt-4o-transcribe`, or `gpt-4o-mini-transcribe` (those use ISO-639-1 / BCP-47 + `prompt`).
- **AI-classified language codes**: The language name (e.g., "Danish"), ISO 639-1 code (e.g., "da"), and transcription phrases are all classified/generated by GPT-5.4-nano at tool-call time via the `language`, `languageCode`, and `transcriptionHint` parameters on `MakePhoneCall`. No hardcoded mapping exists in the codebase — the AI determines the correct values for any language.

### `hang_up` Tool

Registered as a function tool in the Realtime session:

- **Description**: _"End the phone call. IMPORTANT: Always say a friendly goodbye message IN THE SAME LANGUAGE you've been speaking BEFORE calling this tool."_
- **Parameter**: `reason` (required string) — e.g., "conversation complete", "caller said goodbye", "voicemail detected".

### Two-Phase Hang-Up Flow

The hang-up is implemented as a **two-phase process** to guarantee the AI always speaks a farewell before disconnecting:

1. **Phase 1 — Capture intent**: On `response.function_call_arguments.done` with `hang_up`, set `m_pendingHangUp = true`, send `conversation.item.create` (function_call_output with farewell instruction, language-aware), then trigger `response.create` to force the model to speak.
2. **Phase 2 — Disconnect after farewell**: On `response.done` with `m_pendingHangUp == true`, wait ~2 seconds for audio to flush, invoke `m_onHangUp` callback → `CallConnection.HangUpAsync(true)` disconnects all participants.

---

## Azure Resources (Shared with ContactCenterAgent Platform)

All resources are provisioned by the shared `infra/resources.bicep` — no duplicate deployments.

| Resource            | Name                          | Location       | Purpose                                                                             | Bicep Section |
| ------------------- | ----------------------------- | -------------- | ----------------------------------------------------------------------------------- | ------------- |
| AI Foundry          | `cog-contactcenteragent`      | Sweden Central | Shared AI resource — hosts both `gpt-5.4-nano` and `gpt-realtime`                   | §1            |
| Model: gpt-realtime | (deployment on above)         | Global         | Voice AI for caller agent (GA, version 2025-08-28, capacity 10 RPM, GlobalStandard) | §3b           |
| ACS                 | `acs-contactcenteragent`      | Global         | Phone number, call routing, media streaming                                         | §12           |
| Container App       | `ca-caller-agent`             | Sweden Central | Caller agent API (.NET 8, WebSockets, 1 vCPU / 2 GB, 1–3 replicas)                  | §13           |
| Role Assignment     | (on `cog-contactcenteragent`) | —              | Cognitive Services OpenAI User for caller agent Managed Identity                    | §15           |
| ACS Diagnostics     | `diag-contactcenteragent-acs` | —              | All logs + metrics → Log Analytics                                                  | §16           |
| App Insights        | `appi-contactcenteragent`     | Sweden Central | Shared telemetry (used by both admin chat + caller agent)                           | §7            |
| Log Analytics       | `log-contactcenteragent`      | Sweden Central | Shared log sink                                                                     | §4            |

### Resource Consolidation (No Duplication)

- **AI Foundry**: Single `cog-contactcenteragent` resource hosts both `gpt-5.4-nano` (admin chat) and `gpt-realtime` (voice AI). The `gpt-realtime` deployment has `dependsOn: [modelDeployment]` (the gpt-5.4-nano deployment) to avoid parallel deployment conflicts.
- **App Insights + Log Analytics**: Shared across both services — no separate telemetry resources.
- **Authentication**: Both services use **`DefaultAzureCredential`** (ManagedIdentity in Azure, Azure CLI locally) — no API keys for AI Foundry access. Caller agent uses a singleton `TokenCredential` with per-call force-refresh via `claims: "{}"`.

---

## Container App Configuration (injected by Bicep)

| Setting Name                                | Source                                                 | Notes                                                                |
| ------------------------------------------- | ------------------------------------------------------ | -------------------------------------------------------------------- |
| `AcsConnectionString`                       | `acs.listKeys().primaryConnectionString`               | ACS SDK requires connection string (secret ref)                      |
| `AzureOpenAI__Endpoint`                     | `aiFoundry.properties.endpoint`                        | Double underscore = nested config in .NET                            |
| `AzureOpenAI__DeploymentName`               | `realtimeModelDeployment.name`                         | Resolves to `gpt-realtime`                                           |
| `APPLICATIONINSIGHTS_CONNECTION_STRING`     | `appInsights.properties.ConnectionString`              | Well-known env var, SDK auto-detects                                 |
| `AcsPhoneNumber`                            | Set by `provision-phone-number.ps1` post-provision     | E.164 format, e.g. `+18005551234`                                    |
| `NUXT_CALLER_AGENT_URL`                     | `callerAgentApp.properties.configuration.ingress.fqdn` | Used by admin-chat Nuxt proxy for outbound calls + transcription SSE |
| `NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING` | `appInsights.properties.ConnectionString`              | Browser App Insights SDK in admin-chat frontend                      |

---

## Key SDK Packages

| Package                                    | Version | Purpose                                                             |
| ------------------------------------------ | ------- | ------------------------------------------------------------------- |
| `Azure.Communication.CallAutomation`       | 1.5.1   | Call control: answer, create, hang up, media streaming              |
| `Azure.Communication.Common`               | 1.4.0   | `PhoneNumberIdentifier`, `CommunicationIdentifier`                  |
| `Azure.Identity`                           | 1.13.2  | `DefaultAzureCredential` singleton (MI in Azure, CLI locally)       |
| `Azure.Messaging.EventGrid`                | 4.13.0  | Inbound call event parsing (`SubscriptionValidationEventData`)      |
| `Microsoft.ApplicationInsights.AspNetCore` | 2.22.0  | App Insights telemetry integration (`TrackEvent`, `TrackException`) |
| `Newtonsoft.Json`                          | 13.0.3  | JSON serialization for ACS media streaming protocol                 |

### Frontend NPM Package (admin-chat)

| Package                              | Version | Purpose                                                     |
| ------------------------------------ | ------- | ----------------------------------------------------------- |
| `@microsoft/applicationinsights-web` | ^3.3.11 | Browser App Insights SDK (page views, errors, dependencies) |

### SDK Version Notes (verified Feb 2026)

- `Azure.Communication.CallAutomation` 1.5.1 (latest stable). Requires `Azure.Communication.Common` ≥ 1.4.0.
- **No `Azure.AI.OpenAI` package** — the Realtime API connection uses a raw `ClientWebSocket` with bearer token auth, not the OpenAI SDK.
- `Azure.Identity` provides the singleton `DefaultAzureCredential` — auto-selects ManagedIdentity in Azure and Azure CLI locally. Per-call force-refresh via `claims: "{}"` ensures tokens are always fresh.

---

## Audio Pipeline Details

| Stage        | Format         | Details                                                                                                           |
| ------------ | -------------- | ----------------------------------------------------------------------------------------------------------------- |
| ACS → App    | PCM 24kHz mono | WebSocket binary frames, parsed by `StreamingData.Parse()` in `AcsMediaStreamingHandler`                          |
| App → OpenAI | PCM 24kHz mono | Base64-encoded, sent as `input_audio_buffer.append` JSON via raw WebSocket                                        |
| OpenAI → App | PCM 24kHz mono | Received as `response.audio.delta` JSON, base64-decoded, wrapped via `OutStreamingData.GetAudioDataForOutbound()` |
| App → ACS    | PCM 24kHz mono | Sent back via ACS WebSocket as text frames                                                                        |

## Voice Activity Detection (VAD)

- **Type**: `azure_semantic_vad_multilingual` (Azure's semantic VAD with multilingual support — English, Spanish, French, Italian, German, Japanese, Portuguese, Chinese, Korean, Hindi)
- Threshold: **0.5**, prefix padding: **300ms**, silence duration: **500ms** (aligned with official Voice Live API reference example)
- Filler word removal: disabled (`remove_filler_words: false`)
- **Barge-in supported**: when user starts speaking (`input_audio_buffer.speech_started`), AI audio is stopped via `OutStreamingData.GetStopAudioForOutbound()`
- **End-of-Utterance (EOU) detection**: NOT used. The `end_of_utterance_detection` property is only supported for **cascaded pipeline** models (gpt-4o, gpt-4.1, gpt-5) — NOT for `gpt-realtime` (native multimodal). Sending it with `gpt-realtime` causes an `invalid_request_error` that can kill the receive loop.
- **VAD type reference** (Voice Live API `2025-10-01`): `azure_semantic_vad` (English-only), `azure_semantic_vad_multilingual` (10 languages), `server_vad` (energy-based), `semantic_vad` (OpenAI, uses `eagerness` instead of `threshold`/`silence_duration_ms`)

## Voice

- **Voice**: `alloy` (OpenAI built-in voice — consistent accent across languages, no accent switching)
- **Type**: `openai`
- **Temperature**: Not applicable (OpenAI voices don't support temperature)
- **Multilingual**: Follows the language instruction in the system prompt. Chosen over Azure DragonHD because DragonHD was observed to switch accents mid-call on multilingual conversations.

## Audio Processing

- **Noise reduction**: `azure_deep_noise_suppression`
- **Echo cancellation**: `server_echo_cancellation`

## Error Handling (WebSocket Receive Loop)

Per Microsoft docs: _"Most errors are recoverable and the session stays open."_ The `ReceiveMessagesAsync` loop in `AzureVoiceLiveService.cs` implements smart error handling:

- **`session_error`** (fatal): Breaks the receive loop — session is unrecoverable.
- **`invalid_request_error`** and other types (recoverable): Logged via `TrackException()` and `Console.Error`, but the receive loop **continues** processing events. This prevents a single bad request (e.g., unsupported session property) from killing the entire call.

**Important**: Never add a `break` on generic errors — it was the root cause of a production outage where `end_of_utterance_detection` (unsupported for `gpt-realtime`) caused `invalid_request_error` on every `session.update`, killing the receive loop and silencing all AI audio.

## Transcription

- **User speech**: Requires an `input_audio_transcription` block in the `session.update` config — without it, the Realtime API never emits transcription events. Transcription runs asynchronously — does NOT delay AI voice responses. Default is `azure-speech` with BCP-47 `language` (e.g. `da-DK`) and a Norlys `phrase_list`; overridable via `Transcription:Model` to `whisper-1` / `gpt-4o-transcribe` / `gpt-4o-mini-transcribe` (those use `prompt` instead of `phrase_list`).
- Events: `conversation.item.input_audio_transcription.completed` (user), `response.audio_transcript.done` (AI)
- Both logged as custom events to App Insights via `TelemetryClient.TrackEvent()` with dimensions: `chat.event_type`, `chat.content`, `chat.channel` ("voice"), `chat.phone_number`

### Live Transcription Streaming (SSE Pipeline)

Transcription events are streamed **live** to the admin chat frontend via a multi-hop SSE pipeline:

```
AzureVoiceLiveService                CallerAgent.cs              Nuxt Proxy                    Browser
(ChannelWriter.TryWrite)  ──▶  GET /api/transcription/{id}  ──▶  [contextId].get.ts  ──▶  useCallTranscription.ts
                                    (ChannelReader SSE)           (SSE proxy)              → TranscriptionPanel.vue
```

1. **`AzureVoiceLiveService`**: On `conversation.item.input_audio_transcription.completed` (user) and `response.audio_transcript.done` (AI), calls `m_transcriptionWriter.TryWrite(new TranscriptionEvent(...))` with role ("user"/"assistant"), text, and timestamp.
2. **`CallerAgent.cs`**: Stores `Channel<TranscriptionEvent>` per call in `ConcurrentDictionary<string, Channel<TranscriptionEvent>>` keyed by context ID. `GET /api/transcription/{contextId}` reads from `ChannelReader` and emits SSE (`text/event-stream`) events. Channel is cleaned up on call disconnect.
3. **Nuxt proxy** (`server/api/transcription/[contextId].get.ts`): Forwards SSE from caller agent to browser, using `NUXT_CALLER_AGENT_URL` env var for the upstream URL.
4. **`useCallTranscription.ts`** (composable): Opens `EventSource` to the Nuxt proxy, parses incoming SSE events into reactive `transcriptLines` array. **Merges consecutive same-speaker entries within 4 seconds** to reduce fragmentation from VAD splitting mid-sentence. Tracks exceptions via `$appInsights?.trackException()`.
5. **`TranscriptionPanel.vue`**: Right sidebar (desktop) / slide-over with backdrop (mobile) displaying live transcript with role labels and timestamps.

**Record type** (in `CallerAgent.cs`):

```csharp
record TranscriptionEvent(string Role, string Text, DateTime Timestamp);
```

---

## Phone Number Provisioning

### Critical: Azure CLI Limitations

The Azure CLI `az communication phonenumber` extension only supports `list` and `show` commands. **`search` and `purchase` do NOT exist in the CLI.** Phone number search and purchase must use the ACS REST API directly.

### Provisioning Script (`scripts/provision-phone-number.ps1`)

- **REST API version**: `2024-03-01-preview`
- **Authentication**: HMAC-SHA256 using the ACS access key (parsed from connection string)
- **Idempotent**: Checks for existing numbers before provisioning
- **Flow**: Parse connection string → generate HMAC-SHA256 auth headers → `GET /phoneNumbers` to list existing → `POST /availablePhoneNumbers/countries/{country}/search` → poll search status → `POST /availablePhoneNumbers/countries/{country}/search/{id}/purchase` → poll purchase status → update Container App env var → set `azd` env var
- **Default**: US toll-free numbers, configurable via `-CountryCode`, `-PhoneNumberType`, `-AreaCode` parameters
- **Called by**: `azd postprovision` hook and GitHub Actions deploy workflow

---

## Deployment

### azd Integration (`azure.yaml`)

```yaml
services:
  caller-agent:
    project: code/caller-agent
    host: containerapp
    language: docker
    docker:
      path: ./Dockerfile
      context: .
      remoteBuild: true
```

Post-provision hook automatically runs phone number provisioning:

```yaml
hooks:
  postprovision:
    shell: pwsh
    run: ./scripts/provision-phone-number.ps1 -ResourceGroup "rg-contactcenteragent" -AcsResourceName "acs-contactcenteragent" -ContainerAppName "ca-caller-agent"
```

### GitHub Actions (`.github/workflows/deploy.yml`)

**3-job pipeline with parallel builds:**

1. **`infra` job**: Inline change detection (`git diff` on `infra/`), conditionally runs Bicep deploy + phone provisioning only when infra files changed
2. **`build` job**: Matrix strategy builds `admin-chat` and `caller-agent` Docker images on **separate parallel runners**, pushes to ACR with `${{ github.sha }}` and `latest` tags. Uses `docker/build-push-action` with Buildx
3. **`deploy` job**: Parallel Container App updates via background processes (`&` + `wait`), faster ACR login using `--expose-token` + `docker login`

- OIDC authentication for zero-secret CI/CD
- Phone provisioning runs in `infra` job, gated on infra changes

---

## Security Considerations

- **Authentication to AI Foundry**: Singleton `DefaultAzureCredential` (ManagedIdentity in Azure, Azure CLI locally) — no API keys stored or transmitted. Role assignment (Cognitive Services OpenAI User) grants access.
- **ACS connection string**: Injected by Bicep into Container App secrets. Should be moved to Key Vault for production.
- **Phone numbers**: Validated in E.164 format before call initiation.
- **System prompt injection**: The `purpose` field from outbound call requests is embedded into the system prompt — sanitize before production use.
- **Rate limiting**: Consider adding rate limiting on `POST /api/outboundCall` to prevent abuse.
- **Per-call prompts**: Stored in-memory `ConcurrentDictionary<string, string>` keyed by callback context ID. Cleaned up via `TryRemove` when the WebSocket connects.

---

## Local Development

```bash
# Caller Agent API (needs dev tunnel for ACS WebSocket callbacks)
cd code/caller-agent/agent
devtunnel host  # or set VS_TUNNEL_URL environment variable
dotnet run      # Runs on configured port

# ContactCenterAgent admin dashboard
cd code/admin-chat
npm run dev     # Nuxt on :3000, .NET agent on :8000
```

For local testing of the caller agent:

1. Set `VS_TUNNEL_URL` to your dev tunnel URL (ACS needs a public endpoint for WebSocket callbacks)
2. Configure AI Foundry access: `dotnet user-secrets set "AzureOpenAI:Endpoint" "https://cog-contactcenteragent.cognitiveservices.azure.com/"`
3. Set ACS connection string: `dotnet user-secrets set "AcsConnectionString" "<from-portal>"`
4. Set phone number: `dotnet user-secrets set "AcsPhoneNumber" "+18005551234"`
5. For the admin chat agent, set `NUXT_CALLER_AGENT_URL` to the dev tunnel URL

---

## Admin Chat Frontend (Call-Focused UX)

The admin chat UI ([code/admin-chat/pages/index.vue](code/admin-chat/pages/index.vue)) features:

- **AI Octopus mascot** image (`w-80 h-80`, `~/assets/icons/octopus.png`)
- **Heading**: "AI Call Center"
- **Subtitle**: "Your personal AI call center — outbound calls in 90+ languages"
- **1 call-focused suggestion button**:
  - 📞 Make an AI call → "Make an AI call"
- **Powered-by tech strip**: Azure, .NET 10, Agent Framework, AG-UI (with icons)
- **TranscriptionPanel**: Right sidebar showing live call transcript (role labels + timestamps). On mobile, slides over with backdrop overlay. Appears automatically when a call is active.
- **App Insights telemetry**: Browser SDK via `appinsights.client.ts` plugin (`@microsoft/applicationinsights-web`). Auto-tracks page views, fetch/XHR dependencies, unhandled rejections. Vue errors captured via `nuxtApp.hook("vue:error", ...)`. Cloud role: `admin-chat-frontend`. Graceful fallback when no connection string (returns `null`; composables use `$appInsights?.` optional chaining).

### MakePhoneCall Tool Parameters (AdminChatAgent.cs)

| Parameter           | Type   | Required | Description                                                                                                                                                                         |
| ------------------- | ------ | -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `countryCode`       | string | Yes      | Country code without '+' (e.g. '1', '45'). Inferred from country name                                                                                                               |
| `phoneNumber`       | string | Yes      | Phone number without country code. Whitespace auto-stripped                                                                                                                         |
| `talkAboutThis`     | string | Yes      | AI caller's conversation goal/purpose                                                                                                                                               |
| `name`              | string | Yes      | Name of the person being called — AI greets by name                                                                                                                                 |
| `language`          | string | No       | Language to speak (default: "English"). AI-classified, never hardcoded                                                                                                              |
| `languageCode`      | string | No       | ISO 639-1 code (default: "en"). AI-classified — e.g. 'da', 'sv', 'tr', 'ar'                                                                                                         |
| `transcriptionHint` | string | No       | Comma-separated keywords/phrases in the target language for `whisper-1` vocabulary hint. AI-generated, never hardcoded. Keyword lists work best for whisper-1 (not full sentences). |

### OutboundCallRequest Record (CallerAgent.cs)

```csharp
record OutboundCallRequest(string PhoneNumber, string? Purpose, string? SystemPrompt, string? Name, string? Language, string? LanguageCode, string? TranscriptionHint);
```

---

## File Renames

Both `Program.cs` files were renamed for clarity:

| Original                             | Renamed To          | Purpose                                                |
| ------------------------------------ | ------------------- | ------------------------------------------------------ |
| `code/admin-chat/agent/Program.cs`   | `AdminChatAgent.cs` | Admin chat agent (tools, system prompt, MakePhoneCall) |
| `code/caller-agent/agent/Program.cs` | `CallerAgent.cs`    | Caller agent API (ACS, WebSocket, call handling)       |

---

## gpt-realtime Capacity

- **Capacity**: 10 (= 10 RPM, Requests Per Minute)
- **Subscription hard limit**: 10 RPM for `gpt-realtime` GlobalStandard SKU
- Previously set to 100, which caused `InsufficientQuota` deployment failures
- Each RPM = one new Realtime API session per minute (sufficient for demo/dev workloads)

---

## Future Work (Phase 3)

- **Call tracking**: Track active calls in Azure Table Storage (shared `stcontactcenteragent` account)
- **Activity Panel**: Show call duration, status (ringing → connected → ended) in the admin UI

> **Note**: Transcript streaming was implemented — live transcription events are streamed via SSE to the `TranscriptionPanel` component (see Transcription section above).
