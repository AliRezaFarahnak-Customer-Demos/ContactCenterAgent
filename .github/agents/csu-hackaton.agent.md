# CSU Hackathon: Intelligent Commute Agent (Cowork + voice agent while you drive)

The idea: at the end of the day, Microsoft 365 Copilot Cowork scans the employee's Teams messages, emails and open delivery reports. It then uses browser automation to open the "Intelligent Commute Agent" page in admin-chat and place a phone call. The voice agent collects the answers by voice ("you talk faster than you type"). About 5 minutes later, Cowork reads the transcript and result JSON and does the replies and follow-ups.

Purpose (persona: Iben, CSAM): Iben said she spends about 40 minutes in the car each way, so 2 x 40 = 80 minutes a day, where she cannot type. The call reaches her while she drives. It starts with a short "since last night" briefing (what happened after she logged off), then goes through her follow-ups. She answers the open questions and gives quick spoken updates and outcomes of today's engagements, and Cowork then does the replies, report closures and success-plan updates for her. She only approves them when she has arrived. That is where the time is saved.

The page is deliberately unbranded: plain white/blue, system font, neutral favicon, no Norlys or Microsoft branding. It lives at `/reporting`, with aliases `/cowork`, `/value` and `/outcome`, which all render the same page. Page-scoped favicons override the global ones by `key` (see `nuxt.config.ts`).

## Flow

1. Cowork (scheduled task, ~16:00) gathers the open items: unanswered questions, replies needed today, and delivery reports that must be closed to stay compliant.
2. Cowork opens `https://<ca-admin-chat>/reporting` in the user's own Edge. Easy Auth works because it reuses the user's cookie.
3. Cowork fills in `phone-number`, `call-language` and `system-instructions` (it writes today's task list into the template every day, see "System instructions format"), checks the "Today's plan" preview, then clicks `place-call`.
4. The phone rings. The agent works through the tasks one by one. You can also add tasks by voice, e.g. "close the report for Fjordvik Logistics, we made the POC production ready today", which is faster than typing.
5. Cowork opens `/reporting?call=<contextId>` again, waits for `#cowork-page[data-call-done="true"]`, and reads `[data-testid=result-json]`.
6. Cowork carries out each `actions[]` entry with `status: "ready"` through browser automation: closes the report in ESXP, replies in Outlook or Teams. It asks for confirmation before sending or submitting anything.

## Page contract (`code/admin-chat/pages/cowork.vue`)

| Selector | Purpose |
| --- | --- |
| `#cowork-page` | Root. Attributes `data-call-status` (`idle`, `placing`, `ringing`, `in_progress`, `ended`, `completed`, `error`), `data-call-done` (`true`/`false`), `data-context-id` |
| `[data-testid=country-code]` | Country code without `+`, default `45` |
| `[data-testid=phone-number]` | Local number |
| `[data-testid=call-language]` | `<select>` with `da` (Danish, default) or `en` (English) |
| `[data-testid=system-instructions]` | System prompt. A line `Navn: <first name>` replaces `CustomerName` in the greeting |
| `[data-testid=insert-template]` | "Insert template". Inserts the TTS-safe template (security check plus Today / Can wait sections) in the selected language |
| `[data-testid=agenda]` | "Today's plan" preview parsed from the instructions. `[data-testid=agenda-item]` per numbered task with `data-priority` (`today`/`later`) and `data-urgent` |
| `[data-testid=place-call]` | "Call me". Writes `?call=<contextId>` into the URL and stays on the current alias |
| `[data-testid=new-call]` | "New call". Resets the form |
| `[data-testid=call-status]` | Status text in English (`role=status`, `data-status` carries the machine value) |
| `[data-testid=context-id]`, `[data-testid=call-error]`, `[data-testid=call-done]` | Call id, error message, done marker |
| `[data-testid=transcript] li[data-speaker]` | Transcript lines (`ai`, `user`, `system`; shown as Agent / You / System) |
| `[data-testid=call-summary]`, `[data-testid=call-outcome]`, `[data-testid=follow-up-draft]` | AI case summary |
| `[data-testid=call-actions]` | Actions card with `data-action-count` and a "N of M solved" progress bar, grouped into `[data-testid=actions-ready]` (Solved), `actions-open` (Not solved) and `actions-postponed`, each with `data-count` |
| `[data-testid=action]` | One per action. `data-action-type` (`close_report`, `update_report`, `reply_email`, `reply_teams`, `send_email`, `send_teams`, `create_task`, `other`), `data-action-target` (e.g. `Fjordvik Logistics`), `data-action-status` (`ready`/`postponed`/`open`), `data-action-priority` (`today`/`later`). Text in `[data-testid=action-content]` is ready to paste |
| `[data-testid=result-json]` | Full result as JSON, including `actions[]`; the easiest thing for Cowork to read |

Query parameters: `?phone=`, `?cc=`, `?lang=` (`da`/`en`) and `?instructions=` prefill the form. `?example=1` loads the fictional demo scenario (see below) unless `?instructions=` is given. `?call=<contextId>` resumes polling or shows an existing result.

`[data-testid=insert-example]` ("Load example") inserts the same fictional scenario in the selected language.

## System instructions format

Cowork writes the whole system prompt itself every day. "Insert template" shows the exact structure; the page's "Today's plan" preview (`[data-testid=agenda]`) parses it, so Cowork can check it filled the field correctly before calling. Sections, in order:

1. Intro (who the agent is, `Navn: Iben` in Danish), then `HUN KØRER BIL` / `SHE IS DRIVING`: short sentences, no links or numbers read aloud, "wait" pauses the agent, and it hangs up if she cannot talk safely.
2. `ÅBNING` / `OPENING`: greeting plus a one-line hook ("four things today, two must be answered today, the first is urgent").
3. `SIKKERHED` / `SECURITY`: the agent asks for the alias, then for two upcoming engagements, and mentions no task until both are right. One retry, then it apologizes and hangs up. Answers are never revealed.
3b. `SIDEN I GÅR AFTES` / `SINCE YESTERDAY EVENING` (optional): what happened since she logged off, as `- ` bullets. Told briefly after security, before the tasks. The page shows them as "Since last night" (`[data-testid=news] [data-testid=news-item]`).
4. `SKAL SVARES I DAG` / `MUST ANSWER TODAY`: numbered tasks. The agent keeps asking until it has a clear answer; if you insist on postponing, it is recorded as not solved (`open`). Prefix a task with `HASTER.` / `URGENT.` to flag it.
5. `KAN VENTE TIL EN ANDEN DAG` / `CAN WAIT FOR ANOTHER DAY`: numbered tasks, numbering continues. The agent asks "now or another day?"; another day means `postponed`.
6. Rules and closing: re-ask open today items, then a quick-updates round ("any other updates or outcomes from today's engagements?", repeated until no), then summarize solved / postponed / not solved, then `hang_up`.

The case summary receives these instructions too, so `actions[]` contains one entry per numbered task, with `priority` (`today`/`later`) taken from the section and `status` `ready` (solved), `postponed` or `open` (not solved; `content` says what is missing). If the security check fails, `verified` is `false` and `actions` is empty.

## Demo scenario (all names fictional)

The persona is Iben (CSAM, driving). Customers and other people are fictional; never show real customers on stage. The built-in example (`EXAMPLE_PLAN` in `cowork.vue`) has:

- Security: alias `xyz123`; upcoming engagements Birkedal Foods (AI agents workshop, 1 October) and Stormkyst Forsikring (AI hackathon, 8 October).
- Since last night: Havnestad Pension wrote again about the stalled ticket (worried about go-live); Stormkyst sent the hackathon attendee list (12 people); Solbakke Pension shared its architecture diagram in Teams.
- Must answer today (Iben's real kinds of request, with fictional customers and details):
  1. URGENT: a stalled support ticket for Havnestad Pension (stand-in for PFA), six days without movement. Raise the priority or escalate to the duty manager?
  2. Confirm whether a CSA is free on 14 October for a workshop at Solbakke Pension (stand-in for AP Pension). Cowork found Mikkel Dahl (morning) and Laura Kjær (all day).
- Can wait:
  3. Where is Havnestad Pension on Security Copilot? Pilot with 20 users since August; the security review is missing.

Fictional answers Iben gives on the call (driving), and what Cowork does with them afterwards:

| # | Iben says | Expected action | Cowork does (after she approves) |
|---|-----------|-----------------|----------------------------------|
| 1 | "Escalate it to the duty manager, it is blocking their go-live. Tell the customer they get an update by tomorrow noon." | `escalate_ticket`, ready | Drafts the escalation to the support duty manager and a reply to the customer. |
| 2 | "Book Laura for the whole day, and tell the customer she is confirmed." | `book_resource`, ready | Sends Laura the booking and drafts the confirmation to the customer. |
| 3 | "Another day." | `status_update`, postponed | Adds it to tomorrow's call. |
| + | Quick update: "Birkedal Foods confirmed the workshop on the 1st of October." | `status_update`, ready (extra) | Writes it into the engagement notes. |

Security answers: "x y z one two three"; "Birkedal Foods and Stormkyst". Result: "3 of 4 solved" (one postponed).
### Cowork prompt (daily scheduled task, ~16:00)

```text
Daily end-of-day voice debrief. Run every workday at 16:00.

1. Scan my Teams chats, emails and open ESXP delivery reports from today. Find:
   - questions or requests to me that are still unanswered,
   - delivery reports that must be closed or updated to stay compliant.
   For each item decide: MUST ANSWER TODAY (deadline today, urgent, a customer or my manager is waiting)
   or CAN WAIT FOR ANOTHER DAY. Put the most urgent first and prefix it with "URGENT.".
2. Open https://<ca-admin-chat>/reporting in my browser. Select language "da", enter +45 and my phone number.
3. Click "Insert template" and replace the placeholders in the system instructions field:
   - Opening hook: how many items, how many must be answered today, and whether one is urgent.
   - SECURITY: my alias, and two of my upcoming customer engagements from my calendar or ESXP.
   - MUST ANSWER TODAY and CAN WAIT FOR ANOTHER DAY: one numbered line per item, spoken style,
     saying who, what, and what answer is needed. No emojis, markdown or arrows; the text is read aloud.
   Keep all other sections of the template unchanged.
4. Check that "Today's plan" on the page lists every item with the right Today / Can wait badge,
   then click "Call me".
5. While I take the call, reopen the page with ?call=<contextId> every minute until
   #cowork-page has data-call-done="true" (max 15 minutes).
6. Read [data-testid=result-json]. If "verified" is false, do nothing and tell me the security check failed.
   Otherwise show me a table of actions[]: type, target, priority, status, content, and "N of M solved".
7. For each action with status "ready":
   - reply_teams / reply_email: prepare the reply with the content text, addressed to the target.
   - close_report / update_report: fill in the delivery report in ESXP with the content text.
   - create_request / book_resource / escalate_ticket: prepare the request, booking or escalation with the content text.
   Ask me to confirm before anything is sent or submitted.
   For "postponed": add them to tomorrow's debrief list.
   For "open" with priority "today": warn me that it is still not solved and say what is missing.
```

### STAGE DEMO (final): caller page and Cowork side by side, click "Call me" live

No live Cowork integration on stage. Two windows side by side on the TV:

- **Left:** the caller page, https://ca-admin-chat.mangoglacier-49a73362.swedencentral.azurecontainerapps.io/reporting?example=1&lang=en&cc=45&phone=30300857 (hard-reload with Ctrl+Shift+R first). The example prompt, phone +45 30300857 and English are already filled in.
- **Right:** Cowork in the browser, on the "create scheduled task" screen, with the Cowork prompt below pasted in (the "Copy Cowork prompt" button on the page copies it). Name "Intelligent Commute Agent", schedule every workday at 07:30 and 16:00. Show it, but do not run it.

Run of show (about 5 minutes):

1. Say: "Iben drives 40 minutes each way. That is 80 minutes a day where she cannot type."
2. Point right: "Every morning and afternoon, Cowork runs this scheduled task. It finds her follow-ups, writes them into this page and calls her."
3. Point left: "This is what Cowork fills in: today's news and three tasks." Scroll through "Since last night" and "Today's plan".
4. Click **Call me** on the left page. Iben's phone rings within about 30 seconds. Speaker on.
5. Iben answers with the call script below.
6. After hang-up, the page shows the transcript, "2 of 3 solved" and the actions. Point right: "Tomorrow, Cowork reads exactly this and sends the approved messages for her."

Cowork prompt (shown in the scheduled task, not run on stage):

```text
Intelligent Commute Agent: my drive-home follow-up call.

I am Iben, a CSAM at Microsoft. I drive 40 minutes each way and cannot type. Call me, let me approve my follow-ups by voice, then do the work for me.
This is a live demo. Use only the fictional example on the page. Do not open my real mailbox, chats, ESXP or support tickets.

1. Open https://ca-admin-chat.mangoglacier-49a73362.swedencentral.azurecontainerapps.io/reporting?example=1&lang=en&cc=45&phone=30300857 in my browser.
2. Check that "Today's plan" shows three tasks. If not, click "Load example". Then click "Call me".
3. Tell me in one line: "Calling you now. Drive safely."
4. Wait until #cowork-page has data-call-done="true" (check every 30 seconds, max 15 minutes).
5. Read [data-testid=result-json]. If "verified" is false, stop and tell me the security check failed. Do nothing else.
6. Show me: "N of M solved" from the page, a table of actions[] (type, target, status, content), and a two-line summary of the call.
7. For every action with status "ready": I approved it on the call. Show the final Teams message or email with recipient and text, marked "Approved on the call". The customers are fictional, so show it as sent instead of really sending it.
8. For every action with status "postponed" or "open": add it to tomorrow's call list and say "I'll follow up with you tomorrow on the daily call."
```

### Call script for Iben (English, about 2 minutes)

The agent keeps every reply to one or two short sentences and asks one simple question at a time. Before any Teams message or email it drafts it and asks "Shall I send it?".

| # | Agent (roughly) | Iben says |
|---|-----------------|-----------|
| 1 | "Hi Iben, this is your Intelligent Commute Agent. I have some quick news and three quick tasks. Is now a good time?" | "Yes, go ahead." |
| 2 | "What is your alias?" | "x, y, z, one, two, three." |
| 3 | "Can you name two of your upcoming engagements?" | "Birkedal Foods and Stormkyst Insurance." |
| 4 | "Thanks, you're verified." Then three quick news items. | (listen) |
| 5 | Task 1, urgent: the Havnestad Pension ticket is stuck for six days. "Shall I escalate it to the duty manager?" | "Yes, escalate it." |
| 6 | Drafts a one-sentence email to the customer. "Shall I send it?" | "Yes, send it." |
| 7 | Task 2: the Solbakke Pension workshop on the 14th of October needs a CSA. "Shall I book Laura?" | "Yes, book Laura." |
| 8 | Drafts a one-sentence Teams message to the customer. "Shall I send it?" | "Yes, send it." |
| 9 | Task 3: the Havnestad Security Copilot pilot. "What is the status?" | "I don't know yet." |
| 10 | "No problem, I'll follow up with you tomorrow on the daily call." "Anything else for today?" | "No, that's all." |
| 11 | One-sentence summary. "Safe drive, Iben. Bye." Hangs up. | "Bye." |

Expected on the page afterwards: "2 of 3 solved". Ready: escalate_ticket plus the customer email (Havnestad), book_resource plus the Teams message (Solbakke). Postponed: the Havnestad Security Copilot status.

If the agent mishears: say the answer again, a little slower. Say the alias one character at a time.

`done` becomes `true` when the case summary has arrived (usually a few seconds after hang-up), or 60 seconds after hang-up if no summary comes.

## Backend

- `code/caller-agent/agent/CallRecordingStore.cs` stores the transcript and summary per outbound call in memory for 24 hours. It is lost on restart and doesn't work across multiple replicas; that's fine for the demo.
- `GET /api/calls/{contextId}/transcript` on caller-agent. The proxy lives in `code/admin-chat/server/api/calls/[contextId]/transcript.get.ts`.
- Calls are placed through the existing `POST /api/place-call`.
- `actions[]` is extracted at hang-up by the case summary (`CaseSummary.Actions` in `ConversationAnalysisService.cs`, strict JSON schema). Customer-service calls simply get an empty array.

## Deploy

```bash
azd deploy caller-agent
azd deploy admin-chat
```

## Caveats

- Cowork browser use requires the tenant setting "Cowork Browsing" (off by default) and only works in the web client. Fallback: Cowork calls the outreach MCP server (`/mcp`) directly, but each tool call has a 30-second timeout, so it must start the call and then read the result in a later run.
- ESXP is reached through Cowork's browser automation in the user's own session. The call only captures what to write; Cowork does the clicking and submitting.
- `actions[]` is produced by an LLM from the transcript. Cowork should show them for confirmation before submitting anything to ESXP or sending replies.
