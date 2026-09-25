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

### FINAL: Cowork scheduled task for the stage (press "Run now" live)

Create this scheduled task in Cowork before going on stage. Name: "Intelligent Commute Agent". Schedule: every workday at 07:30 and 16:00. On stage, open the task and press "Run now". Iben's phone (+45 30300857) rings within about 30 seconds.

```text
Intelligent Commute Agent: my morning and evening drive briefing.

Context: I am Iben, a CSAM at Microsoft. I drive about 40 minutes each way, so 80 minutes a day where I cannot type. Use that time: call me, let me answer my follow-ups by voice, then do the work for me.
This is a live demo. Use only the fictional example on the page. Do not open my real mailbox, chats, ESXP or support tickets.

1. Open https://ca-admin-chat.mangoglacier-49a73362.swedencentral.azurecontainerapps.io/reporting?example=1&lang=en&cc=45&phone=30300857 in my browser.
2. Check the page before calling:
   - Country code is 45, phone number is 30300857, language is English.
   - "Since last night" shows three items.
   - "Today's plan" shows three tasks: two Today (the first marked Urgent) and one Can wait.
   If anything is wrong, click "Load example" and check again. Then click "Call me".
3. Tell me in one line: "Calling you now. Drive safely." Keep this page open while I take the call.
4. Wait until #cowork-page has data-call-done="true" (check every 30 seconds, max 15 minutes).
5. Read [data-testid=result-json].
   - If "verified" is false: stop, and tell me the security check failed. Do nothing else.
   - Otherwise show me:
     a) one headline: "N of M solved" from the page,
     b) a table of actions[]: type, target, priority, status, content,
     c) a three-line summary of the call.
6. Do the work, as drafts only:
   - escalate_ticket: draft the escalation to the support duty manager and the reply to the customer.
   - book_resource: draft the booking to the CSA and the confirmation to the customer.
   - reply_email / reply_teams: draft the reply to the target.
   - status_update with status "ready": write it as an engagement note.
   - postponed: put it on tomorrow morning's call list.
   - open with priority today: warn me it is still not solved and say what is missing.
7. End with: "Everything is drafted. Say yes and I will send it." Never send or submit anything before I say yes.
```

What Iben says on the call (in English, speaker on, as if driving): security "x, y, z, one, two, three", then "Birkedal Foods and Stormkyst Insurance"; then the answers in the call script below for tasks 1 to 3; then the quick update "Birkedal Foods confirmed the workshop on the 1st of October"; then "no, that's all". Expected on screen: "3 of 4 solved" and Cowork's drafts.
### Call script for Iben (English, about 3 minutes)

Speaker on, phone in hand as if it sits in the car holder. The agent's lines are approximate; Iben's lines are what she says. Speak calmly, short sentences, and wait for the agent to finish before answering.

| # | Agent (roughly) | Iben says |
|---|-----------------|-----------|
| 1 | "Hi Iben, it's your assistant with today's follow-ups. I have a little news from last night and three tasks. Two must be answered today, and the first one is urgent. Do you have five minutes?" | "Yes, I'm in the car. Go ahead." |
| 2 | "Great. First two security questions. What is your alias?" | "x, y, z, one, two, three." |
| 3 | "Can you name two of your upcoming engagements?" | "Birkedal Foods and Stormkyst Insurance." |
| 4 | News: Havnestad wrote again last night, Stormkyst sent the attendee list with twelve people, Solbakke shared its architecture diagram. "Now to today's tasks." | (listen, say nothing) |
| 5 | Task 1, urgent: the Havnestad Pension ticket has not moved for six days. Raise the priority or escalate, and what should the customer be told? | "Escalate it to the duty manager. It is blocking their go-live. Tell the customer they get an update by tomorrow noon." |
| 6 | Repeats it back and asks if that is right. | "Yes, exactly." |
| 7 | Task 2: a CSA on the 14th of October for Solbakke Pension. Mikkel Dahl in the morning or Laura Kjær all day? | "Book Laura for the whole day, and tell the customer she is confirmed." |
| 8 | Task 3 (can wait): Havnestad Pension and Security Copilot. Now or another day? | "Let's take that one another day." |
| 9 | "Any other quick updates or outcomes from today's engagements that I should write down or close?" | "Yes. Birkedal Foods confirmed the workshop on the 1st of October." |
| 10 | "Anything else?" | "No, that's all." |
| 11 | Summary: two solved plus one extra update, one postponed. Cowork takes care of the rest, approve when you arrive. Safe drive, hangs up. | "Thanks, bye." |

Optional wow moment, between steps 6 and 7: Iben says "Wait a second, I'm going into a roundabout." The agent answers "of course, I'll wait" and goes quiet. After a few seconds she says "Okay, I'm back", and the agent picks up where it left off. This shows it is built for driving.

If the agent mishears: just say the answer again, a little slower. If it repeats something back wrong: "No, it's ..." and the right answer. Say the alias one character at a time, not as one word.
Expected on the TV afterwards: "3 of 4 solved". Solved: escalate_ticket (Havnestad), book_resource (Solbakke), and the Birkedal Foods status_update. Postponed: the Havnestad Security Copilot status_update.
### Stage script: set it up as a Cowork scheduled task, then run it now (wow version)

Iben pastes step 1 into Cowork on the TV. Cowork explains scheduled tasks and asks a few questions; she answers with the short lines in step 2, then says "run it now" and takes the phone call live.

Step 1, paste into Cowork:

```text
I want to set up a scheduled task. Briefly explain how scheduled tasks work in Cowork, then ask me a few questions to figure out what I'd like Cowork to do and when it should run.

Some context so your questions are sharp: I am a CSAM. I drive about 40 minutes to work and 40 minutes home, so 80 minutes a day where I cannot type. I want Cowork to use that time. Before each drive, Cowork should find what happened since I logged off and which follow-ups need an answer from me, then call me through my Intelligent Commute Agent page so I can answer by voice. When the call is done, Cowork reads the result and does the work for me.

For this demo use only the fictional example on the page. Do not open my real mailbox, chats or reports.
```

Step 2, Iben's answers to Cowork's questions (say or type them, one per question):

- What should it do? "Brief me on what happened since last night, then call me and go through my follow-ups: support tickets, CSA bookings and customer status questions."
- When should it run? "Every workday at 7:30, when I start the car. And once more at 16:00 on the way home."
- Where does it call me? "Use my Intelligent Commute Agent page: https://ca-admin-chat.mangoglacier-49a73362.swedencentral.azurecontainerapps.io/reporting?example=1&lang=en&cc=45&phone=30300857. English."
- What should it do after the call? "Wait until the page says the call is done. Show me the actions and how many are solved. Draft the booking and the escalation. Never send anything before I say yes."
- Anything it must not do? "No real customer data in this demo, and never send or submit without my approval."

Step 3, after Cowork confirms the schedule, paste:

```text
Great. Run it once right now, so I can see it work.
Open the page, check that "Since last night" has three items and "Today's plan" has two Today items (the first Urgent) and one Can wait item, then click "Call me".
While I take the call, keep the page open and wait until #cowork-page has data-call-done="true".
Then read [data-testid=result-json], show me a table of actions[] (type, target, status, content) and the "N of M solved" count,
and draft what each "ready" action needs. Do not send or submit anything. Put "postponed" items on tomorrow morning's call.
```

Step 4, the phone rings. Iben picks up (speaker on, as if driving) and answers with the lines in the table above: security "x y z one two three" and "Birkedal Foods and Stormkyst", then the three tasks, then the quick update about Birkedal Foods. Expected result on screen: "3 of 4 solved" and Cowork's drafts.

Line for the room while it rings: "80 minutes of productivity, added to my daily commute. That is 80 minutes where my follow-ups get done."
### Cowork prompt (stage demo, fictional data only)

```text
End-of-day debrief (demo). Use only fictional data; do not open my real mailbox or real reports.

1. Open https://ca-admin-chat.mangoglacier-49a73362.swedencentral.azurecontainerapps.io/reporting?example=1&lang=en&cc=45&phone=30300857 in my browser.
2. Check that "Since last night" shows three items and "Today's plan" shows two Today items (the first Urgent) and one Can wait item, then click "Call me".
3. Reopen the page with ?call=<contextId> every minute until #cowork-page has data-call-done="true".
4. Read [data-testid=result-json] and show me a table of actions[] (type, target, priority, status, content)
   and the "N of M solved" count.
5. For "ready" actions: draft the reply or write the report closure text. Do not send or submit.
   For "postponed": create a reminder for tomorrow. For "open": tell me what is still missing.
6. Ask me to confirm before anything is sent or submitted.
```

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
