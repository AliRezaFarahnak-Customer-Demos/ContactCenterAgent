# Norlys demo — run sheet

Live 2026-07-30. Everything below has been executed end to end, not just implemented.

|               | URL                                                                                   |
| ------------- | ------------------------------------------------------------------------------------- |
| Dashboard     | https://ca-admin-chat.mangoglacier-49a73362.swedencentral.azurecontainerapps.io/      |
| Service / MCP | https://ca-caller-agent.mangoglacier-49a73362.swedencentral.azurecontainerapps.io/mcp |

---

## 0. Pre-flight (5 min before)

```powershell
az account set -s acc16e9d-6885-487b-bcd4-632985736970   # context drifts, always re-set
Invoke-RestMethod -Uri "https://ca-caller-agent.mangoglacier-49a73362.swedencentral.azurecontainerapps.io/api/channels"
```

All three channels must report `enabled=True`. Then:

- Open the dashboard, hit **Slet alle** for a clean session list.
- Open Gmail (or whichever inbox you'll show) side by side.
- Phone on the table, ringer on.

> **Do not deploy or restart anything after this point.** Sessions live in memory on this
> tenant (Cosmos is network-blocked above the subscription), so a redeploy wipes the dashboard.

---

## 1. Frame it (30 seconds)

> "This is the caller-agent PoC evolved into a multi-channel outreach service. Your onboarding
> agent hands a customer, a channel and an intent to it — over REST or MCP — and gets structured
> output back. Same Azure Communication Services underneath, plus one managed session per customer
> across voice, SMS and email."

---

## 2. Outbound email, written by the AI

In the dashboard → **E-mail** tab → name + email → **Send udgående**.

Leave the message box **empty** and let it draft from the intent — that's the point worth making:

> "I gave it an intent and a case number, not a template. It wrote the Danish itself, from the
> persona prompt in `personas.json`."

Show the mail landing in the inbox. Sender reads **Norlys Kundeservice**.

---

## 3. The customer replies — the AI answers in the thread

Reply naturally, e.g. _"Ja, min adresse er Hovedgaden 12."_

Within ~30s the agent replies asking for the postcode. Then reply _"8000 Aarhus C"_ and it
confirms the **full** address — proving it carried context across turns.

> "The whole thread is the session. Every reply replays the conversation, so it never loses
> the thread — and it extracts structured fields as it goes."

---

## 4. The money shot — email triggers a real phone call

Reply: **"Okay, ring til mig på +45 XX XX XX XX."**

The agent calls its `place_phone_call` tool → **the phone rings**. Answer it: the voice agent
already knows the case, because the written thread is injected into its system prompt.

> "It didn't just reply — it acted. And the voice agent picks up mid-case rather than starting cold."

Then show the timeline in **Kundesessioner**: email ⇄ email ⇄ voice in one customer session.

---

## 5. Agent-to-agent (MCP)

Bottom of the dashboard: the MCP URL + the nine tools. If a laptop is connected to it, run
`list_channels` then `start_outreach`, and show the new session appear live in the dashboard.

> "Anonymous Streamable HTTP today for the PoC — front it with your APIM MCP registry for auth
> and discovery. No code change."

---

## 6. Close on portability

```powershell
azd env set PHONE_COUNTRY DK
azd env set ACS_DATA_LOCATION Europe
azd up
```

> "One command on your subscription. It provisions everything, buys the Danish number, and wires
> the Event Grid subscriptions for inbound. Resource names are auto-suffixed per subscription, so
> it deploys cleanly alongside ours."

---

## Say this before they ask

- **SMS to Danish numbers** — the service sends SMS today, but a US toll-free can't deliver to
  +45; that's carrier regulation, not Azure. Production path is a **Danish mobile number**, which
  is **GA in ACS with two-way SMS** — the catch is that Danish toll-free and local numbers are
  voice-only, so only the `Mobile` type carries SMS there. Requires an eligible agreement (MCA,
  CSP, EA, PAYG) and a billing address in the DK-mobile allow-list. **Messaging Connect** (ACS's
  partner route via Infobip) is the fallback if that isn't available, and a branded alphanumeric
  sender covers outbound-only. Same ACS API in every case, so it's config, not code.
- **Written channels collect, voice converses.** Recommend SMS as a deflection to the AI phone
  line rather than a chat channel — better CX and it plays to the voice agent's strength.
- **Email deliverability** — the demo tenant is a sandbox with no SPF/DKIM. From `norlys.dk` this
  is ordinary corporate mail.
- **Preview components** — Messaging Connect is in public preview (no SLA; Microsoft's guidance is
  not to use it for production workloads). Native Danish mobile numbers are GA.

---

## If something breaks

| Symptom                   | Cause / fix                                                                                      |
| ------------------------- | ------------------------------------------------------------------------------------------------ |
| Dashboard sessions empty  | Something restarted the container (in-memory store). Re-send one; don't panic.                   |
| Email doesn't arrive      | Check Junk. Graph reports success even when Exchange drops it — ACS delivery is what we rely on. |
| Reply not threaded        | Poller runs every 30s. Give it a minute before touching anything.                                |
| Call doesn't ring         | Check `AcsPhoneNumber` is E.164 and the number isn't in use by another call.                     |
| Sender shows `donotreply` | An `azd provision` ran without a following `azd deploy` — the placeholder image is serving.      |

---

## Numbers

|                                                |                                               |
| ---------------------------------------------- | --------------------------------------------- |
| Voice (US toll-free, reaches +45)              | +1 833 256 2495                               |
| Danish voice number (purchased, not yet wired) | +45 88 74 30 91                               |
| Email sender                                   | `kundeservice@<managed-domain>.azurecomm.net` |
| Replies land in                                | `admin@mngenvmcap566671.onmicrosoft.com`      |
