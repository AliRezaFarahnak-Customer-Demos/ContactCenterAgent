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

Hard rules: headlines bold + left-aligned only · never `font-mono` in production UI (use `tabular-nums` for digits) · logo top-right or bottom-left · red is an accent, never dominant · WCAG AA contrast (≥ 4.5:1 body, ≥ 3:1 large) · Danish UI tone: enkelhed, handlekraft, optimisme; English for code/errors.

---

## Persistent gotchas (durable, not config)

These are workflow hazards that don't depend on which model/voice/version is current:

- **OIDC app reg gets nuked nightly** in MngEnv tenants. If `azure/login` fails with "Not all values are present", re-run `./scripts/setup-oidc.ps1` (rewrites `AZURE_CLIENT_ID` / `TENANT_ID` / `SUBSCRIPTION_ID` on the `production` GitHub environment). Allow ~2 min for RBAC to propagate before the next deploy.
- **Easy Auth on `ca-admin-chat` is locked to a single user.** Auth config (set via `az rest PUT` to `authConfigs/current`, NOT `az containerapp auth microsoft update` — that command wipes the registration block and leaves the sidecar crash-looping with "upstream connect error / Connection refused"):
  - `openIdIssuer` pinned to the tenant (`https://login.microsoftonline.com/<tenantId>/v2.0`), NOT `/common/`. The auto-provisioned default uses `/common/` + `signInAudience=AzureADandPersonalMicrosoftAccount` and that combo breaks the auth sidecar.
  - `defaultAuthorizationPolicy.allowedApplications: []` (the auto-provisioned default lists the app's own clientId there, which forces app-only tokens and rejects all user logins).
  - `defaultAuthorizationPolicy.allowedPrincipals.identities: ["<my-user-oid>"]` so only the listed user(s) get past `/.auth`. Add more OIDs here to grant access; don't open it up tenant-wide.
  - Symptom of a broken auth sidecar (vs a crashed app): `az containerapp revision list` shows the app revision Running with replicas=1, but the URL returns Envoy's "upstream connect error or disconnect/reset before headers … Connection refused". Fix is always to re-PUT the full authConfig JSON, never to patch individual fields with `--set` or `auth microsoft update`.
- **CI must write `.version.json` itself** before `docker build` — the file isn't committed; locally it comes from the azd prepackage hook. Build job needs `fetch-depth: 0` so `git rev-list --count HEAD` returns the real count.
- **Test the right call.** When verifying a deploy, check the App Insights `Session accepted by server: …` trace timestamp against the GitHub Actions deploy completion time. A call placed before the new revision rolled out will hit the old container and look broken.
- **Voice Live silently falls back to defaults** if you send unsupported fields (e.g. `temperature` on `azure-standard` voices). After any voice/STT change, verify via the App Insights `Session accepted by server` trace that the `voice` / `input_audio_transcription` block in `session.updated` matches what you sent.
- **PSTN narrowband is a hard physical limit on STT** (0.3–3.4 kHz G.711). No prompt, phrase-list, or model swap can recover frequencies the network discarded. Phone-call transcripts will always look weaker than laptop-mic transcripts in `danish-voice-lab/`. The biggest-impact fix is a Custom Speech model trained on real call recordings; cheaper alternative is a parallel Azure Speech telephony recognizer for the admin-UI transcript only (see `app-architecture-dependencies.agent.md` for the sketched approach).
- **Wire payload > SDK choice.** caller-agent talks Voice Live as raw JSON; danish-voice-lab uses the typed SDK. The server can't tell them apart — don't rewrite one to "match" the other.
- **Greeting "wait for caller" pattern is custom and load-bearing for the demo.** Default Voice Live behaviour is "AI keeps talking until interrupted, then auto-replies the moment caller stops." For an outbound call where the AI greets first, that gives a bad UX (PSTN line noise / an early "hi" cuts off "Hej Mette…", and any speech during the greeting gets queued and replied to instantly when greeting ends). The fix in `AzureVoiceLiveService` has multiple cooperating pieces — DON'T rip any one of them out without understanding the full chain:
  1. Initial `session.update` sends `turn_detection.interrupt_response: false` AND `create_response: false` (server won't cancel TTS, won't auto-fire the next AI turn).
  2. Client-side `speech_started` handler is gated on `m_greetingInFlight` — does NOT send `StopAudio` or `response.cancel` during greeting (otherwise WE kill the greeting even though the server wouldn't).
  3. `response.done` for the FIRST turn schedules a `Task.Delay` based on bytes-of-greeting-audio-streamed (Voice Live generates audio faster than realtime; `response.done` fires in ~1s but PSTN playback takes ~4s for a typical greeting). Use `m_greetingDelayScheduled` so subsequent `response.done`s don't reschedule.
  4. After the delay: send `input_audio_buffer.clear` to throw away any caller speech captured during the greeting → re-send `session.update` with normal `turn_detection` (defaults restore `interrupt_response: true`, `create_response: true`) → DO NOT manually fire `response.create`. Server VAD will pick up the next fresh utterance and respond naturally.
  5. Audio-bytes math: Voice Live source is 24 kHz pcm16 → 48000 bytes/sec. Add ~300ms safety tail for ACS RTP jitter. Don't change to 16 kHz math — that's the ACS-side rate, the BYTES we count are still pre-resample 24 kHz.
- **Verification false-positives are a prompt problem, not a code problem.** The model will accept "h ja det harjer" as a correct address if the rule says "accept obvious STT errors." Use the structured ACCEPT/REJECT/UKLART block in `personas.json` with concrete examples (e.g. "1983 vs 1982 = REJECT, that's a wrong year not an STT mishearing"). Re-validate after any prompt edit by saying obviously-wrong values on a test call and checking the App Insights `User transcript` / `AI transcript` traces.
- **`personas.json` is a TTS prompt — write it like one.** Whatever you put in the prompt, the model tends to mirror in its replies, and replies are read aloud by Voice Live. Hard rules when editing either `personas.json` (root, source of truth) OR `code/admin-chat/server/personas.json` (the SPA copy that's actually shipped — keep both in sync, see comment in `code/admin-chat/server/utils/personasStore.ts`):
  1. **No emojis anywhere in the prompt body** (⚠️ ✅ ❌ 🎉 etc). Use plain Danish labels: `VIGTIGT:`, `EKSEMPEL PÅ KORREKT:`, `EKSEMPEL PÅ FORKERT:`. The `# STIL` block already forbids emojis in replies — don't contradict it in the instructions.
  2. **No markdown bold (`**...**`), no `→`arrows, no`\*` bullets.** TTS reads asterisks and arrows literally. Use plain dashes `-` for bullets and the word "så" instead of `→`.
  3. **No "space-comma-space" artifacts** (`KONKRET , 2 til 4 sætninger`). They're leftovers from a script that mangled em-dashes; the model occasionally mirrors the awkward spacing into its output.
  4. **First-name policy:** the opening rule says use fornavn ÉN GANG. The afslutning line `Tak fordi du er kunde hos Norlys, CustomerName, hav en rigtig god dag.` deliberately uses it a second time — that's fine and natural for a Danish goodbye. Don't "fix" the contradiction by stripping the name from the close.
  5. **Branched opening flow is load-bearing.** The `# ÅBNING — NATURLIG FLOW MED FORGRENINGER` block has four named branches (A: yes → ack + verify; B: caller asks purpose first → 1-sentence purpose then "har du tid?"; C: caller declines → respect, no callback offer, warm close, `hang_up`; D: unclear → repeat opener). Don't collapse them back to a linear "step 1, 2, 3" script — the original linear version made the model jump straight from "ja" to "Før vi går videre, må jeg lige bede dig bekræfte din adresse?" with no acknowledgement, which sounds robotic on a real call. Verified from App Insights `AI transcript` traces.
  6. **MFA is non-negotiable on any "yes" path.** Both Gren A and Gren B → ja MUST go through 2 security questions before talking case details. Don't add a "skip MFA if purpose was disclosed in B" shortcut.

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

---

## Voice quality — HD Omni reference (current direction)

We are migrating from `da-DK-ChristelNeural` (standard neural) to **Dragon HD Omni** for richer Danish prosody. Test in `danish-voice-lab/` first, then promote to caller-agent.

**Reference links (read these first, don't trust this doc's snapshot):**

- Dragon HD Omni voice catalog (700+ voices, source of truth): https://github.com/Azure-Samples/Cognitive-Speech-TTS/blob/master/Blog-Samples/Introducing-Dragon-HD-Omni/dragonhdomni_voice_list.json
- Voice Live "How to" (HD voice payload examples, region list): https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to
- Voice Live API reference 2025-10-01 (`RealtimeAzureStandardVoice` schema): https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-api-reference-2025-10-01
- HD voices article (regions, supported voices): https://learn.microsoft.com/en-us/azure/ai-services/speech-service/high-definition-voices

**Confirmed Danish HD Omni voices** (verified in catalog JSON above):

- `da-DK-Christel:DragonHDOmniLatestNeural` — F Adult, polished/professional (news/corporate)
- `da-DK-Jeppe:DragonHDOmniLatestNeural` — M Young Adult, smooth/authoritative

There is **no Danish voice in the non-Omni DragonHD set** — Omni is the only HD path for `da-DK`. Standard neural multilingual variants (`*MultilingualNeural`) are also rejected by Voice Live `azure-standard`.

**Region requirement:** HD voices only run in `southeastasia, centralindia, swedencentral, westeurope, eastus, eastus2, westus2`. Our resource is in **Sweden Central** ✓.

**How to wire it (Voice Live JSON / SDK):**

```json
"voice": {
  "type": "azure-standard",
  "name": "da-DK-Christel:DragonHDOmniLatestNeural",
  "temperature": 0.7
}
```

The `:DragonHDOmniLatestNeural` suffix on the `name` is what flips the server into HD mode — `type` stays `azure-standard`. The `Azure.AI.VoiceLive` SDK 1.0.0 surfaces this via `new AzureStandardVoice(name) { Temperature = 0.7f }`. Same `AzureStandardVoice` class carries both standard neural and HD Omni — only the name string differs.

**What you can tune through Voice Live (vs. raw Speech SDK):**

- ✅ `temperature` (0.0–1.0) — only meaningful on HD/HD Omni; silently ignored on standard neural. 0.7 = calm customer service; 0.8 = more variation.
- ✅ `rate` (0.5–1.5 string) — works on both standard and HD.
- ✅ `pitch`, `volume`, `style`, `locale`, `prefer_locales`, `custom_lexicon_url` — all on `RealtimeAzureStandardVoice`.
- ❌ `top_p` / `top_k` / `cfg_scale` — these are HD Omni `parameters=` SSML attributes for direct Speech SDK use. **Not exposed through Voice Live JSON.**
- ⚠️ `mstts:express-as` styles (cheerful, empathetic, friendly, professional, customer-service, etc.) — **DOCS SAY English-only, BUT empirically work on `da-DK` HD Omni** (lab-verified 29 Apr 2026 via `code/speech-tool/ danish-lab` against direct Speech SDK). Voice Live exposes `style` on `RealtimeAzureStandardVoice` and accepts the field. **Caveat:** styles aren't reachable through the Voice Live JSON wire payload that `caller-agent` builds today — `BuildVoiceConfig` would need a `style` field added. Validate on PSTN before relying on it: laptop-speaker results may overstate the effect once G.711 strips fidelity.
- ✅ Paralinguistic tokens (`[sighing]`, `[laughter]`, `[breathing]`) — work in all languages, including Danish (lab-confirmed). Probably inappropriate for Norlys customer service.
- ⚠️ The `instructions` system prompt only weakly steers Azure-voice prosody (per docs: _"may not apply to Azure voices"_). HD Omni's automatic prosody prediction does the heavy lifting; pacing should come from punctuation in model output.

**PSTN ceiling still applies on output.** HD's quality gain is full-bandwidth on a laptop speaker but compressed through G.711 0.3–3.4 kHz on a phone call. The benefit on PSTN is mostly _prosody/intonation/pause naturalness_, not raw fidelity. Test in `danish-voice-lab/` with headphones to hear the full upside; expect a smaller (but still real) win on actual ACS calls.

**Where it lives in our code:**

- `code/shared/ContactCenterAgent.Shared/VoiceLive/VoiceLiveDefaults.cs` — single source of truth for `DefaultVoiceName` (= `da-DK-Christel:DragonHDOmniLatestNeural`), `DefaultVoiceTemperature` (= 0.7), and the `IsHdVoice(name)` helper that gates the `temperature` field on the wire payload.
- `danish-voice-lab/appsettings.json` — minimal: only Endpoint / Model / PersonaId / MicDevice. Voice/temperature inherited from shared. Override `Voice` here to A/B test without touching prod.
- `danish-voice-lab/Program.cs` — reads voice/temperature with shared defaults; auto-applies `DefaultVoiceTemperature` when voice is HD and no override given. `BuildAzureStandardVoice(name, temperature?)` constructs the typed SDK payload (Temperature property only set when non-null).
- `code/caller-agent/agent/AzureVoiceLiveService.cs` `BuildVoiceConfig` — defaults from shared; sends `temperature` on `azure-standard` ONLY when `IsHdVoice(name)` returns true (so standard neural never gets an unexpected field that could trigger silent fallback).
- `code/caller-agent/agent/appsettings.json` — explicit override holds prod on `da-DK-ChristelNeural` until lab validation completes. **To promote HD Omni to prod: delete the `Voice.Type` / `Voice.Name` / `Voice.Temperature` lines** and prod will inherit shared defaults. Redeploy with `azd deploy caller-agent`.

**Verification after any voice swap:** App Insights `Session accepted by server` trace must echo the exact voice name in the `session.updated.voice` block. If it shows the default voice instead of what you sent → silent fallback (unsupported field or wrong region).

---

## Deferred Voice Live customizations (not yet wired)

Audited against the [official Voice Live customization docs](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to-customize) — these official options are NOT in the codebase yet. Listed in order of ROI for Norlys / Danish PSTN. Pick up when there's a complaint that maps to one of them.

**Add when we wire real backend tools / DB lookups:**

- **TÆNKEPAUSE prompt section** ("et øjeblik, lad mig lige tjekke") — was added then removed because the agent currently has no tools other than `hang_up`, so there's nothing to wait on. Adding the hint without a real wait reason just teaches the model to insert unnecessary verbal filler. The block to re-add (under `# LYT` in each persona in `personas.json` AND `code/admin-chat/server/personas.json`) is:
  ```
  # TÆNKEPAUSE — UNDGÅ STILHED
  - Hvis du har brug for et øjeblik til at tænke eller slå noget op, sig det HØJT i stedet for at være tavs. Eksempler: "et øjeblik, lad mig lige tjekke," "hmm, lad mig se," "så lige et sekund."
  - Brug det MAKSIMALT en gang i ny og næ — ikke i hver tur. Stilhed på telefon er værre end en kort verbal pause; hyppige fyldord er værre end stilhed.
  ```
  Trigger: as soon as we add a function-calling tool that can take >300 ms (e.g. `lookup_customer_account(phone)`, billing lookup, knowledge-base search). Re-add then. Cheaper alternative when we get there: Voice Live's native `interim_response` feature — but it's docs-confirmed incompatible with realtime audio models, so the prompt-driven version above is what we'll actually use.

1. **`custom_lexicon_url`** on `voice` block (TTS pronunciation control) — host an SSML-format lexicon XML at a public URL, point `RealtimeAzureStandardVoice.custom_lexicon_url` at it. Use case: force correct pronunciation of "Norlys", Danish street-name suffixes (-vej, -gade, -allé), postal-code patterns, common foreign brand names. Lower effort than Custom Voice; biggest fix for branded mispronunciations on PSTN. Same lexicon works for both standard neural and HD Omni. Docs: [custom lexicon for text to speech](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-synthesis-markup-pronunciation#custom-lexicon).
2. **`prefer_locales: ["da-DK", "en-US"]`** on `voice` block — when Christel emits an embedded English word inside a Danish sentence (brand name, technical term), this pins the _secondary_ English accent to American. Without it the accent on the foreign word is unpredictable. One-line addition to `BuildVoiceConfig` / `BuildAzureStandardVoice`; pair with a new `Voice:PreferLocales` config key.
3. **Custom Speech model** trained on real Norlys call recordings — wire via `input_audio_transcription.custom_speech: { "da-DK": "<modelId>" }` (per the customize doc). The ONLY meaningful upgrade for PSTN STT quality (the G.711 narrowband ceiling can't be undone by phrase_list or model swap). Multi-week project: collect ≥10h of real call audio with consent, train in Speech Studio on the SAME AI Foundry resource we use for Voice Live (cross-resource = must copy model first), pay separately for training + hosting. Docs: [What is custom speech?](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/custom-speech-overview).
4. **`rate: "1.05"`** on `voice` block — small speed bump to reduce perceived hesitation between AI sentences if testers feel Christel sounds slow. Trivial; taste-dependent. Range 0.5–1.5 (string).
5. **Parallel admin-UI Azure Speech recognizer** — separate `da-DK` recognizer on the same audio, ONLY for the displayed transcript in admin-chat. Doesn't change what the gpt-realtime model "hears" (Voice Live STT stays in place for the model). Sketched in `app-architecture-dependencies.agent.md`. Cheaper than Custom Speech if all you need is a more readable live transcript.

Things deliberately NOT pursued (would hurt or are unsupported for `da-DK`):

- ~~`mstts:express-as` styles~~ — was previously listed as English-only per the docs; **lab tests on 29 Apr 2026 showed they DO affect Danish on HD Omni** (`friendly`, `professional`, `customer-service` were all audibly different from baseline). Now a candidate for prod, blocked only on adding `style` to `BuildVoiceConfig` and PSTN validation. See `code/speech-tool/Program.cs` (`danish-lab` command, variants `60-cs-*` and `80-narrated-*`).
- `remove_filler_words` — English-only feature.
- `gender` / `age` / `description` from the Dragon HD Omni catalog JSON — those are catalog metadata, NOT wire fields. Sending them risks silent fallback.
- Sending `temperature` on standard neural voices — already gated by `IsHdVoice()` because docs warn unsupported fields can trigger silent fallback.
