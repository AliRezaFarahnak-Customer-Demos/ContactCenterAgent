# Contact Center Agent — Copilot Instructions

Multi-service platform on Azure: **admin-chat** (Nuxt 3 SPA, no .NET backend) and **caller-agent** (.NET 10, ACS + Voice Live API). Deployed via `azd` to Azure Container Apps. Local `azd up` is allowed.

Voice Live reference code: https://github.com/microsoft-foundry/voicelive-samples/tree/main/csharp

---

## How to orient yourself at the start of a session

This file deliberately contains **no code-level facts** (file lists, package versions, prompts, model names, voice configs, VAD numbers). Those drift. Discover the current state from the source instead — every time:

1. **Project layout** — `list_dir` the repo root, then drill into the folder relevant to the task. Top-level shape is `code/{admin-chat,caller-agent,speech-tool,shared}/`, `danish-voice-lab/`, `infra/`, `scripts/`, `azure.yaml`. `personas.json` at repo root is the one source of truth for persona prompts.
2. **Shared library** — `code/shared/ContactCenterAgent.Shared/` is referenced by both `caller-agent` and `danish-voice-lab`. Owns: `Personas/PersonaStore.cs` (loads `personas.json`), `VoiceLive/NorlysDanishPhrases.cs` (Danish STT phrase_list), `VoiceLive/VoiceLiveDefaults.cs` (VAD numbers, model name, voice name, sample rate). Change here once → both surfaces pick it up. Never re-inline these in a consumer.
3. **Voice / STT / VAD / voice prompt** — read `code/caller-agent/agent/appsettings.json` and `code/caller-agent/agent/AzureVoiceLiveService.cs` (look for `BuildTranscriptionConfig`, `BuildVoiceConfig`, `UpdateSessionAsync`). Defaults come from `VoiceLiveDefaults`; appsettings only holds overrides.
4. **Per-call prompt + MFA flow** — read `code/admin-chat/server/api/place-call.post.ts` (system prompt is built here, then sent to caller-agent verbatim).
5. **Sandboxes** — `danish-voice-lab/Program.cs` is the laptop-mic Danish sandbox the user has tuned by ear; production caller-agent should mirror its wire payload (uses the same `ContactCenterAgent.Shared` defaults).
6. **Models / deployments / SKUs / regions** — read `infra/resources.bicep` (Bicep is the source of truth, not docs).
7. **NuGet versions / target framework** — read the relevant `*.csproj`. Don't trust any version numbers cached in markdown.
8. **Known traps for a current task** — `grep_search` for the topic in `.github/agents/*.agent.md` (those _can_ go stale too — verify against source before quoting).

When the user asks "what voice/model/prompt are we using?" → open the actual file. Never answer from memory or from this doc.

---

## Local development

```bash
# Admin Chat (Nuxt SPA only — single process)
cd code/admin-chat && npm install && npm run dev          # port 3000

# Caller Agent (needs devtunnel for ACS callbacks)
cd code/caller-agent/agent
$env:ASPNETCORE_ENVIRONMENT = "Development"               # loads user-secrets
dotnet run --urls "http://localhost:5000"
```

- **Never build Docker locally** (dev machine is ARM64). Use `azd deploy` (remote ACR build) or `az acr build` as fallback.
- Keep each service's `.dockerignore` tight (`node_modules`, `bin/`, `obj/`, `.nuxt/`, `.output/`, `dist/`, `.git/`) — the source tarball upload is the slow part on flaky internet, not the build.
- Requires **azd ≥ 1.24.1** (1.23.6 has a BlobNotFound bug).

## Deploy

```bash
azd up                    # provision + deploy both services
azd deploy admin-chat     # SPA only
azd deploy caller-agent   # phone agent only (e.g. after voice/prompt tweak)
```

- `code/admin-chat/.version.json` is rewritten by the **root-level** `prepackage` hook in `azure.yaml` (`1.0.<commit-count>-<sha>`). Don't move it to a service-scoped hook — that silently skipped in the past. CI also writes this file itself before `docker build` (see `.github/workflows/deploy.yml`); the JSON schema must match `nuxt.config.ts` (`appVersion`, `buildNumber`).
- After deploy, hard-reload (`Ctrl+Shift+R`) or use incognito — Nuxt SPA + service worker cache aggressively.
- **Deployed env vars override `appsettings.json`.** Always check `az containerapp show … --query "properties.template.containers[0].env"` before assuming the JSON is what's running.

---

## Norlys Branding (CVI 2025)

Read official guidelines **before** editing anything visual in `code/admin-chat/`. The current color tokens, font stacks, logo rules, favicon set, and accessibility contracts live in:

- `code/admin-chat/tailwind.config.ts` — `norlys-*` color tokens (use these, never hardcoded hex)
- `code/admin-chat/assets/css/main.css` — font stack + brand styles
- `code/admin-chat/public/` — official Norlys "Brand O" favicon set (do **not** replace) + logo SVGs

Design system docs (norlys.design — gated, fonts fall back to Georgia / Arial):

| Topic            | URL                                                          |
| ---------------- | ------------------------------------------------------------ |
| Hub              | https://norlys.design/                                       |
| Visuel identitet | https://norlys.design/document/307                           |
| Logo             | https://norlys.design/document/307#/grundelementer/logo      |
| Farver           | https://norlys.design/document/307#/grundelementer/farver    |
| Typografi        | https://norlys.design/document/307#/grundelementer/typografi |
| Digital design   | https://norlys.design/document/332                           |
| Ikoner (403 stk) | https://norlys.design/document/295                           |

Hard rules: headlines bold + left-aligned only · never `font-mono` in production UI (use `tabular-nums` for digits) · logo top-right or bottom-left, respektafstand = 1× Brand O (small) / 1.5× (≥A4), width ≤ 50% format · red is an accent, never dominant · icons monochrome ≤ 40px (default 20px), inherit text color, never recolored/rotated · text color: Warm Grey on Sand/Light Petroleum/light, Sand on Red/Dark Petroleum/dark · WCAG AA contrast (≥ 4.5:1 body, ≥ 3:1 large) · mobile-first, "synligt næste modul" on scroll, less-is-more whitespace · Danish UI tone: enkelhed, handlekraft, optimisme; English for code/errors.

Brand principles to evoke (don't quote, just feel): enkelhed, handlekraft, optimisme + underspillet fællesskab/ansvarlighed/nytænkning.

---

## Persistent gotchas (durable, not config)

These are workflow hazards that don't depend on which model/voice/version is current:

- **OIDC app reg gets nuked nightly** in MngEnv tenants. If `azure/login` fails with "Not all values are present", re-run `./scripts/setup-oidc.ps1` (rewrites `AZURE_CLIENT_ID` / `TENANT_ID` / `SUBSCRIPTION_ID` on the `production` GitHub environment). Allow ~2 min for RBAC to propagate before the next deploy.
- **CI must write `.version.json` itself** before `docker build` — the file isn't committed; locally it comes from the azd prepackage hook. Build job needs `fetch-depth: 0` so `git rev-list --count HEAD` returns the real count.
- **Test the right call.** When verifying a deploy, check the App Insights `Session accepted by server: …` trace timestamp against the GitHub Actions deploy completion time. A call placed before the new revision rolled out will hit the old container and look broken.
- **Voice Live silently falls back to defaults** if you send unsupported fields (e.g. `temperature` on `azure-standard` voices). After any voice/STT change, verify via the App Insights `Session accepted by server` trace that the `voice` / `input_audio_transcription` block in `session.updated` matches what you sent.
- **PSTN narrowband is a hard physical limit on STT** (0.3–3.4 kHz G.711). No prompt, phrase-list, or model swap can recover frequencies the network discarded. Phone-call transcripts will always look weaker than laptop-mic transcripts in `danish-voice-lab/`. The biggest-impact fix is a Custom Speech model trained on real call recordings; cheaper alternative is a parallel Azure Speech telephony recognizer for the admin-UI transcript only (see `app-architecture-dependencies.agent.md` for the sketched approach).
- **Wire payload > SDK choice.** caller-agent talks Voice Live as raw JSON; danish-voice-lab uses the typed SDK. The server can't tell them apart — don't rewrite one to "match" the other.

---

## Removed: AdminChat .NET sidecar

The Nuxt SPA used to ship with a co-located .NET 10 sidecar (port 8000) that exposed an AG-UI streaming chat endpoint via Microsoft Agent Framework. The chat panel and sidecar were removed because they weren't on the demo's critical path (placing/monitoring outbound voice calls). To restore:

1. Recreate `code/admin-chat/agent/` with `AdminChat.csproj` (Microsoft.Agents.AI.Hosting.AGUI.AspNetCore + Azure.AI.OpenAI + Azure.Monitor.OpenTelemetry.AspNetCore) and `AdminChatAgent.cs` exposing the AG-UI endpoint at `/`.
2. Re-add `code/admin-chat/composables/useAgentChat.ts` (SSE consumer), `code/admin-chat/components/ChatMessage.vue`, and `code/admin-chat/server/api/agent.post.ts` (proxy that reads `agentUrl` runtime config and forwards to the sidecar).
3. In `code/admin-chat/package.json`, restore the `concurrently` dev script + `dev:agent`/`install:agent` scripts and the `concurrently` devDependency. Re-add `agentUrl` to `runtimeConfig` in `nuxt.config.ts`.
4. In `code/admin-chat/Dockerfile`, restore the multi-stage build with the .NET sidecar (aspnet:10.0 base + Node copied in, `CMD dotnet agent/AdminChat.dll & node .output/server/index.mjs`).
5. In `infra/resources.bicep`, re-add to the admin-chat container env: `AGENT_URL`, `ASPNETCORE_URLS`, `AzureOpenAI__Endpoint`, `AzureOpenAI__Model`, `AppInsights__ApplicationId`, `CallerAgent__Url`, `OTEL_SERVICE_NAME`. Re-add the `adminChatOpenAIRole` role assignment (Cognitive Services OpenAI User on `aiFoundry`).
6. Restore the chat panel + welcome block in `code/admin-chat/pages/index.vue` (the right-hand `<div class="flex-1 …">` and its script setup using `useAgentChat`). Shrink the sessions aside back to a fixed width (`w-[28rem] xl:w-[32rem]`).

Last working revision before removal: check `git log -- code/admin-chat/agent/AdminChat.csproj`.

---

## Memory

Use `/memories/repo/` for repo-scoped facts you've verified from source this session and want to remember. Update or remove entries that turn out to be stale. Don't write code-level snapshots into this file — keep them in source where they live.
