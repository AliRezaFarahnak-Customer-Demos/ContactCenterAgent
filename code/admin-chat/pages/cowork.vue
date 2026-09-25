<script setup lang="ts">
/**
 * Reporting Automation Agent — a deliberately plain, unbranded, automation-friendly call page.
 *
 * Built for Microsoft 365 Copilot Cowork browser use (and any other agent that
 * drives a browser): every control has a stable id, label and data-testid, the
 * call state is exposed as text and as data attributes, and the result can be
 * re-read later via /reporting?call=<contextId> (aliases: /cowork, /value, /outcome).
 *
 * Also reachable at /reporting, /value and /outcome. Optional prefill query params: phone, cc, lang (da|en), instructions.
 */
definePageMeta({ alias: ["/reporting", "/value", "/outcome"] });

type Entry = { speaker: string; text: string; timestamp: string };
type Snapshot = {
  contextId: string;
  phoneNumber: string;
  status: "ringing" | "in_progress" | "ended" | "completed";
  done: boolean;
  startedUtc: string;
  endedUtc: string | null;
  entries: Entry[];
  transcriptText: string;
  summary: string | null;
  outcome: string | null;
  topics: string[] | null;
  followUpNeeded: boolean | null;
  followUpDraft: string | null;
  actions: Action[] | null;
};
type Action = {
  type: string;
  target: string;
  content: string;
  status: "ready" | "postponed" | "open";
  priority?: "today" | "later";
};
type PageStatus =
  | "idle"
  | "placing"
  | "ringing"
  | "in_progress"
  | "ended"
  | "completed"
  | "error";
type Lang = "da" | "en";

const APP_NAME = "Intelligent Commute Agent";
const FAVICON =
  "data:image/svg+xml," +
  encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="8" fill="#2563eb"/><path d="M9 17l5 5 9-11" fill="none" stroke="#fff" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  );

useHead({
  title: APP_NAME,
  // Larger root size so the whole page reads on a TV screen.
  htmlAttrs: { style: "font-size: 20px" },
  // Same keys as the app-wide favicons in nuxt.config.ts, so Unhead replaces them on this page.
  link: ["favicon-ico", "favicon-32", "favicon-16", "apple-touch-icon"].map(
    (key) => ({
      key,
      rel: key === "apple-touch-icon" ? "apple-touch-icon" : "icon",
      type: "image/svg+xml",
      href: FAVICON,
    }),
  ),
  meta: [{ name: "robots", content: "noindex" }],
});

const route = useRoute();
const router = useRouter();

// Voice prompts are read aloud by TTS: no emojis, no markdown bold, no arrows.
// All people and companies in the example are fictional.
type CallPlan = {
  hook: string;
  today: string[];
  later: string[];
  news: string[];
  alias: string;
  engagements: string[];
};

const HEADERS: Record<Lang, { today: string; later: string }> = {
  da: { today: "SKAL SVARES I DAG", later: "KAN VENTE TIL EN ANDEN DAG" },
  en: { today: "MUST ANSWER TODAY", later: "CAN WAIT FOR ANOTHER DAY" },
};

const PROMPT_TEXT: Record<
  Lang,
  {
    intro: string;
    driving: string;
    opening: (hook: string) => string;
    security: (alias: string, engagements: string[]) => string;
    news: (items: string[]) => string;
    rules: string;
  }
> = {
  da: {
    intro: `Du er Intelligent Commute Agent for Iben, der er Customer Success Account Manager hos Microsoft. Du ringer, mens hun kører hjem, for at klare dagens opfølgninger. Tal dansk. Hvert svar er højst en eller to korte sætninger. Stil ét enkelt spørgsmål ad gangen. Cowork sender de godkendte beskeder efter opkaldet.

Navn: Iben`,
    driving: `HUN KØRER BIL
- Hun kan ikke se en skærm eller skrive. Læs aldrig links, numre eller mailadresser op.
- Siger hun "vent" eller skal fokusere på trafikken, så sig "selvfølgelig, jeg venter" og vær stille, til hun taler igen.
- Kan hun ikke tale sikkert, så sig "helt i orden, jeg følger op med dig i morgen på det daglige opkald", sig farvel og brug hang_up.`,
    opening: (hook) => `ÅBNING
- Sig: "Hej Iben, det er din Intelligent Commute Agent.${hook} Passer det nu?"
- Hvis nej: sig "helt i orden, jeg følger op med dig i morgen på det daglige opkald", sig farvel og brug hang_up.
- Hvis ja: gå til SIKKERHED. Nævn ingen opgaver før sikkerheden er godkendt.`,
    security: (alias, engagements) => `SIKKERHED
- Spørg: "Først, hvad er dit alias?"
- Korrekt alias: ${alias}. Godkend kun præcis de tegn, også når de siges ét ad gangen.
- Spørg derefter: "Og nævn to af dine kommende engagementer."
- Hendes kommende engagementer:
${engagements.map((e) => `  - ${e}`).join("\n")}
- Godkend når hun nævner begge kunder.
- Forkert er: ét forkert tegn i aliaset, kun ét engagement, eller en kunde der ikke står på listen.
- Afslør aldrig svarene og giv ingen hints. Hun må prøve én gang mere. Er det stadig forkert, så sig "desværre, jeg kan ikke gennemgå opgaverne i dag", sig farvel og brug hang_up.
- Når begge er rigtige, så sig "tak, du er bekræftet."`,
    news: (items) => `SIDEN I GÅR AFTES
Sig "kort nyt." og derefter én kort sætning pr. punkt. Spørg ikke om noget her.
${items.map((n) => `- ${n}`).join("\n")}
Sig derefter "så til dine opgaver."`,
    rules: `FOR HVER OPGAVE
- Én opgave ad gangen, i rækkefølge. Sig opgaven i én kort sætning og stil det enkle spørgsmål fra opgaven.
- Korte svar som "ja", "nej", "Laura" eller "ved ikke" er nok.
- Når der skal sendes en besked: sig udkastet i én kort sætning og spørg "skal jeg sende den?"
- Ja: sig "godt, Cowork sender den." og gå videre.
- Nej eller en rettelse: ret udkastet én gang og spørg igen.
- Ved hun det ikke, kan hun ikke beslutte sig, eller vil hun vente: sig "okay, jeg følger op med dig i morgen på det daglige opkald." og gå videre. Pres ikke.
- Gentag og forklar ikke mere end nødvendigt.

AFSLUTNING
- Spørg: "Andet til i dag?" Nævner hun noget, så bekræft det i én sætning og spørg "skal jeg sende den?" på samme måde.
- Opsummér i én sætning, for eksempel: "to beskeder godkendt, én opfølgning i morgen."
- Sig "god tur, Iben. Hej hej." og brug hang_up.`,
  },
  en: {
    intro: `You are the Intelligent Commute Agent for Iben, a Customer Success Account Manager at Microsoft. You call her on her drive home to clear today's follow-ups. Speak English. Keep every reply to one or two short sentences. Ask one simple question at a time. Cowork sends the approved messages after the call.`,
    driving: `SHE IS DRIVING
- She cannot look at a screen or type. Never read out links, numbers or email addresses.
- If she says "wait" or needs to focus on the traffic, say "of course, I'll wait" and stay quiet until she speaks again.
- If she cannot talk safely, say "no problem, I'll follow up with you tomorrow on the daily call", say goodbye and use hang_up.`,
    opening: (hook) => `OPENING
- Say: "Hi Iben, this is your Intelligent Commute Agent.${hook} Is now a good time?"
- If no: say "no problem, I'll follow up with you tomorrow on the daily call", say goodbye and use hang_up.
- If yes: go to SECURITY. Do not mention any task before security has passed.`,
    security: (alias, engagements) => `SECURITY
- Ask: "First, what is your alias?"
- Correct alias: ${alias}. Accept only exactly those characters, also when spoken one by one.
- Then ask: "And name two of your upcoming engagements."
- Her upcoming engagements:
${engagements.map((e) => `  - ${e}`).join("\n")}
- Accept when she names both customers.
- Wrong means: one wrong character in the alias, only one engagement, or a customer not on the list.
- Never reveal the answers and give no hints. She may try once more. If it is still wrong, say "sorry, I can't go through the tasks today", say goodbye and use hang_up.
- When both are right, say "thanks, you're verified."`,
    news: (items) => `SINCE YESTERDAY EVENING
Say "quick news." and then one short sentence per point. Do not ask anything here.
${items.map((n) => `- ${n}`).join("\n")}
Then say "now your tasks."`,
    rules: `FOR EACH TASK
- One task at a time, in order. Say the task in one short sentence and ask the simple question from the task.
- Short answers like "yes", "no", "Laura" or "not sure" are enough.
- When a message is needed: say the draft in one short sentence, then ask "shall I send it?"
- Yes: say "great, Cowork will send it." and move on.
- No or a change: adjust the draft once and ask again.
- If she doesn't know, can't decide, or wants to wait: say "okay, I'll follow up with you tomorrow on the daily call." and move on. Do not push.
- Never repeat or explain more than needed.

CLOSING
- Ask: "Anything else for today?" If she adds something, confirm it in one sentence and ask "shall I send it?" the same way.
- Summarize in one sentence, for example: "two messages approved, one follow-up tomorrow."
- Say "safe drive, Iben. Bye." and use hang_up.`,
  },
};

const BLANK_PLAN: Record<Lang, CallPlan> = {
  da: {
    hook: "",
    today: [
      "Luk leverancerapport for Kunde ... Spørg: \"Kan jeg lukke den?\"",
      "Svar i Teams til ... om ... Spørg: \"Hvad skal jeg svare?\"",
    ],
    later: ["Svar på mail til ... om ... Spørg: \"Hvad skal jeg svare?\""],
    news: ["..."],
    alias: "...",
    engagements: ["...", "..."],
  },
  en: {
    hook: "",
    today: [
      "Close the delivery report for Customer ... Ask: \"Can I close it?\"",
      "Reply in Teams to ... about ... Ask: \"What should I reply?\"",
    ],
    later: ["Reply by email to ... about ... Ask: \"What should I reply?\""],
    news: ["..."],
    alias: "...",
    engagements: ["...", "..."],
  },
};

// Fictional demo day for Iben (CSAM), built from three real kinds of request she gets.
// Customers and people are fictional stand-ins; the context is what Cowork would have found in Teams, Outlook and the support portal.
const EXAMPLE_PLAN: Record<Lang, CallPlan> = {
  da: {
    hook: " Jeg har kort nyt og tre hurtige opgaver.",
    today: [
      "HASTER. Supportsagen for Maersk har stået stille i seks dage. Spørg: \"Skal jeg eskalere den til vagthavende?\" Tilbyd derefter en mail til kunden om at sagen er eskaleret.",
      "Workshoppen hos Novo Nordisk den 14. oktober mangler en CSA. Laura Smith er ledig hele dagen, Mike Jones kun om formiddagen. Spørg: \"Skal jeg booke Laura?\" Tilbyd derefter en Teams-besked til kunden om at bookingen er bekræftet.",
    ],
    later: [
      "Security Copilot-piloten hos Maersk mangler stadig sikkerhedsgennemgangen. Spørg: \"Hvad er status?\" Tilbyd derefter en Teams-besked til kundeteamet med status.",
    ],
    news: [
      "LEGO har sendt deltagerlisten til hackathonet, tolv personer.",
      "Carlsberg takkede for sidste uges Copilot-udrulning.",
    ],
    alias: "xyz123, udtales x, y, z, et, to, tre",
    engagements: [
      "Carlsberg, workshop om AI-agenter den 1. oktober",
      "LEGO, AI-hackathon den 8. oktober",
    ],
  },
  en: {
    hook: " I have some quick news and three quick tasks.",
    today: [
      "URGENT. The Maersk support ticket has been stuck for six days. Ask: \"Shall I escalate it to the duty manager?\" Then offer an email to the customer saying it is escalated.",
      "The Novo Nordisk workshop on the 14th of October needs a CSA. Laura Smith is free all day, Mike Jones only in the morning. Ask: \"Shall I book Laura?\" Then offer a Teams message to the customer confirming the booking.",
    ],
    later: [
      "The Maersk Security Copilot pilot is still missing its security review. Ask: \"What is the status?\" Then offer a Teams message to the account team with the status.",
    ],
    news: [
      "LEGO sent the hackathon attendee list, twelve people.",
      "Carlsberg said thanks for last week's Copilot rollout.",
    ],
    alias: "xyz123, spoken as x, y, z, one, two, three",
    engagements: [
      "Carlsberg, AI agents workshop on the 1st of October",
      "LEGO, AI hackathon on the 8th of October",
    ],
  },
};
function buildPrompt(l: Lang, plan: CallPlan): string {
  const t = PROMPT_TEXT[l];
  const h = HEADERS[l];
  const list = (tasks: string[], offset: number) =>
    tasks.map((task, i) => `${offset + i + 1}. ${task}`).join("\n");
  return [
    t.intro,
    t.driving,
    t.opening(plan.hook),
    t.security(plan.alias, plan.engagements),
    ...(plan.news.length ? [t.news(plan.news)] : []),
    `${h.today}\n${list(plan.today, 0)}`,
    `${h.later}\n${list(plan.later, plan.today.length)}`,
    t.rules,
  ].join("\n\n");
}

const templateFor = (l: Lang) => buildPrompt(l, BLANK_PLAN[l]);
const exampleFor = (l: Lang) => buildPrompt(l, EXAMPLE_PLAN[l]);

type AgendaItem = { n: number; text: string; priority: "today" | "later"; urgent: boolean };

// Reads the numbered tasks under the priority headers so the page can show the call plan.
function parseAgenda(text: string): AgendaItem[] {
  const todayHeaders = [HEADERS.da.today, HEADERS.en.today];
  const laterHeaders = [HEADERS.da.later, HEADERS.en.later];
  const items: AgendaItem[] = [];
  let section: AgendaItem["priority"] | null = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    if (todayHeaders.some((h) => line.startsWith(h))) section = "today";
    else if (laterHeaders.some((h) => line.startsWith(h))) section = "later";
    else if (/^[A-ZÆØÅ ,'.]+$/.test(line)) section = null;
    else if (section) {
      const m = line.match(/^(\d+)\.\s+(.*)$/);
      if (!m) continue;
      const urgent = /^(HASTER|URGENT)\.?\s*/.test(m[2]);
      items.push({
        n: Number(m[1]),
        text: m[2].replace(/^(HASTER|URGENT)\.?\s*/, ""),
        priority: section,
        urgent,
      });
    }
  }
  return items;
}

// Reads the "- " bullets under the since-yesterday header.
function parseNews(text: string): string[] {
  const headers = ["SIDEN I GÅR AFTES", "SINCE YESTERDAY EVENING"];
  const items: string[] = [];
  let inside = false;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    if (headers.includes(line)) inside = true;
    else if (/^[A-ZÆØÅ ,'.]+$/.test(line)) inside = false;
    else if (inside && line.startsWith("- ") && line !== "- ...") items.push(line.slice(2));
  }
  return items;
}

const LANGS: Record<Lang, { label: string; language: string }> = {
  da: { label: "Danish", language: "Danish" },
  en: { label: "English", language: "English" },
};

// Native US English HD Omni voice (catalog: en-us-ava:DragonHDOmniLatestNeural).
const ENGLISH_VOICE = "en-US-Ava:DragonHDOmniLatestNeural";

// The Cowork task that drives this page; the copy button puts it on the clipboard with the current URL.
function coworkPrompt(): string {
  const q = new URLSearchParams({ example: "1", lang: lang.value, cc: countryCode.value });
  const digits = phone.value.replace(/\D/g, "");
  if (digits) q.set("phone", digits);
  const url = `${window.location.origin}${route.path}?${q.toString()}`;
  return `Intelligent Commute Agent: my drive-home follow-up call.

I am Iben, a CSAM at Microsoft. I drive 40 minutes each way and cannot type. Call me, let me approve my follow-ups by voice, then do the work for me.
This is a live demo. Use only the fictional example on the page. Do not open my real mailbox, chats, ESXP or support tickets.

1. Open ${url} in my browser.
2. Check that "Today's plan" shows three tasks. If not, click "Load example". Then click "Call me".
3. Tell me in one line: "Calling you now. Drive safely."
4. Wait until #cowork-page has data-call-done="true" (check every 30 seconds, max 15 minutes).
5. Read [data-testid=result-json]. If "verified" is false, stop and tell me the security check failed. Do nothing else.
6. Show me: "N of M solved" from the page, a table of actions[] (type, target, status, content), and a two-line summary of the call.
7. For every action with status "ready": I approved it on the call. Show the final Teams message or email with recipient and text, marked "Approved on the call". The customers are fictional, so show it as sent instead of really sending it.
8. For every action with status "postponed" or "open": add it to tomorrow's call list and say "I'll follow up with you tomorrow on the daily call."`;
}

const copied = ref(false);
async function copyCoworkPrompt() {
  const text = coworkPrompt();
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const ta = document.createElement("textarea");
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand("copy");
    ta.remove();
  }
  copied.value = true;
  setTimeout(() => (copied.value = false), 2000);
}

const countryCode = ref(String(route.query.cc ?? "45").replace(/[^\d]/g, "") || "45");
const phone = ref(String(route.query.phone ?? ""));
const lang = ref<Lang>(route.query.lang === "en" ? "en" : "da");
const instructions = ref(
  route.query.instructions
    ? String(route.query.instructions)
    : route.query.example !== undefined
      ? exampleFor(lang.value)
      : "",
);

const status = ref<PageStatus>("idle");
const errorMessage = ref("");
const contextId = ref<string>(String(route.query.call ?? ""));
const snapshot = ref<Snapshot | null>(null);

const statusLabel: Record<PageStatus, string> = {
  idle: "Ready",
  placing: "Starting call",
  ringing: "Ringing",
  in_progress: "In progress",
  ended: "Ended, waiting for summary",
  completed: "Completed",
  error: "Error",
};

const isBusy = computed(() =>
  ["placing", "ringing", "in_progress"].includes(status.value),
);
const isDone = computed(() => snapshot.value?.done === true);
const canCall = computed(
  () =>
    !isBusy.value &&
    phone.value.replace(/[^\d]/g, "").length >= 6 &&
    instructions.value.trim().length > 0,
);
const resultJson = computed(() =>
  snapshot.value ? JSON.stringify(snapshot.value, null, 2) : "",
);
const readyCount = computed(
  () => snapshot.value?.actions?.filter((a) => a.status === "ready").length ?? 0,
);
const agenda = computed(() => parseAgenda(instructions.value));
const statusOf = (item: AgendaItem) =>
  item.urgent
    ? { label: "Urgent", dot: "bg-red-600", text: "text-red-700" }
    : item.priority === "today"
      ? { label: "Today", dot: "bg-amber-500", text: "text-amber-700" }
      : { label: "Can wait", dot: "bg-emerald-600", text: "text-emerald-700" };
const news = computed(() => parseNews(instructions.value));
// First sentence only, so the plan reads as headlines on a big screen.
const headline = (text: string) => text.split(/(?<=[.?!])\s+(?=[A-ZÆØÅ])/)[0];
const STATUS_GROUPS = [
  { key: "ready", label: "Solved", badge: "bg-green-50 text-green-700 ring-green-200" },
  { key: "open", label: "Not solved", badge: "bg-red-50 text-red-700 ring-red-200" },
  { key: "postponed", label: "Postponed", badge: "bg-amber-50 text-amber-800 ring-amber-200" },
] as const;
const actionGroups = computed(() =>
  STATUS_GROUPS.map((g) => ({
    ...g,
    items: (snapshot.value?.actions ?? [])
      .filter((a) => (a.status ?? "open") === g.key)
      .sort((a, b) => (a.priority === "later" ? 1 : 0) - (b.priority === "later" ? 1 : 0)),
  })),
);
const ACTION_LABELS: Record<string, string> = {
  close_report: "Close report",
  update_report: "Update report",
  reply_email: "Reply to email",
  reply_teams: "Reply in Teams",
  send_email: "Send email",
  send_teams: "Send Teams message",
  create_task: "Create task",
  create_request: "Create request",
  book_resource: "Book resource",
  escalate_ticket: "Escalate ticket",
  status_update: "Status update",
  other: "Other",
};
const actionLabel = (t: string) => ACTION_LABELS[t] ?? t;
const statusTone = computed(() => {
  if (status.value === "error") return "bg-red-50 text-red-700 ring-red-200";
  if (status.value === "completed") return "bg-green-50 text-green-700 ring-green-200";
  if (isBusy.value || status.value === "ended") return "bg-blue-50 text-blue-700 ring-blue-200";
  return "bg-slate-100 text-slate-600 ring-slate-200";
});

let pollTimer: ReturnType<typeof setTimeout> | null = null;
let missesInARow = 0;

function stopPolling() {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = null;
}

async function poll() {
  if (!contextId.value) return;
  try {
    const snap = await $fetch<Snapshot>(
      `/api/calls/${encodeURIComponent(contextId.value)}/transcript`,
    );
    missesInARow = 0;
    snapshot.value = snap;
    status.value = snap.status;
    if (snap.done) {
      stopPolling();
      return;
    }
  } catch {
    // The recording is created before ACS dials, so repeated misses mean an unknown/expired id.
    if (++missesInARow >= 5) {
      status.value = "error";
      errorMessage.value =
        "Call not found. The id is unknown, older than 24 hours, or the agent was restarted.";
      stopPolling();
      return;
    }
  }
  pollTimer = setTimeout(poll, 2000);
}

async function placeCall() {
  if (!canCall.value) return;
  stopPolling();
  status.value = "placing";
  errorMessage.value = "";
  snapshot.value = null;
  try {
    const res = await $fetch<{
      success: boolean;
      contextId?: string;
      error?: string;
    }>("/api/place-call", {
      method: "POST",
      body: {
        personaLabel: APP_NAME,
        phoneNumber: phone.value,
        countryCode: countryCode.value,
        prompt: instructions.value,
        language: LANGS[lang.value].language,
        // A native English voice and en-US STT, otherwise the Danish default voice speaks English with an accent.
        languageCode: lang.value === "en" ? "en-US" : lang.value,
        ...(lang.value === "en" ? { voice: ENGLISH_VOICE } : {}),
      },
    });
    if (!res.success || !res.contextId) {
      throw new Error(res.error || "No call id returned");
    }
    contextId.value = res.contextId;
    status.value = "ringing";
    // Put the id in the URL so the result can be re-opened later (e.g. by Cowork after the call).
    // Explicit path keeps alias routes (/reporting, /value, /outcome) instead of jumping to /cowork and remounting.
    await router.replace({ path: route.path, query: { call: res.contextId } });
    poll();
  } catch (err: unknown) {
    status.value = "error";
    const data = (err as { data?: { error?: string } })?.data;
    errorMessage.value =
      data?.error || (err instanceof Error ? err.message : String(err));
  }
}

function resetForm() {
  stopPolling();
  contextId.value = "";
  snapshot.value = null;
  status.value = "idle";
  errorMessage.value = "";
  router.replace({ path: route.path, query: {} });
}

function speakerLabel(speaker: string) {
  if (speaker === "ai") return "Agent";
  if (speaker === "user") return "You";
  return "System";
}

onMounted(() => {
  if (contextId.value) {
    status.value = "ringing";
    poll();
  }
});
onBeforeUnmount(stopPolling);

// Overrides the global brand font variables from main.css for this page only.
const SYSTEM_FONT =
  "'Segoe UI', system-ui, -apple-system, Roboto, 'Helvetica Neue', Arial, sans-serif";
const card = "bg-white rounded-2xl border border-slate-200 p-8";
const heading = "block text-lg font-bold text-slate-500 uppercase tracking-wider";
const field =
  "w-full px-4 py-3 text-xl rounded-lg border border-slate-300 bg-white focus:outline-none focus:border-blue-500 focus:ring-2 focus:ring-blue-500/20 disabled:bg-slate-50 disabled:text-slate-500";
</script>

<template>
  <div
    id="cowork-page"
    data-testid="cowork-page"
    :data-call-status="status"
    :data-call-done="isDone ? 'true' : 'false'"
    :data-context-id="contextId || undefined"
    class="h-dvh overflow-y-auto bg-slate-50 text-slate-900"
    :style="{
      '--font-body': SYSTEM_FONT,
      '--font-headline': SYSTEM_FONT,
      fontFamily: SYSTEM_FONT,
    }"
  >
    <header class="h-32 bg-white border-b border-slate-200">
      <div class="max-w-[1800px] mx-auto h-full px-10 flex items-center gap-6">
        <span class="text-6xl font-extrabold tracking-tight text-slate-900">{{ APP_NAME }}</span>
        <img
          src="/copilot.svg"
          alt="Copilot Cowork"
          data-testid="copilot-logo"
          class="h-20 w-auto ml-4"
          draggable="false"
        />
        <span class="text-6xl font-extrabold tracking-tight text-slate-900">CoWork</span>
      </div>
    </header>

    <main class="max-w-[1800px] mx-auto px-10 py-10 space-y-8">
      <div>
        <h1 class="text-7xl font-extrabold tracking-tight text-left leading-tight">
          Enable <span class="text-blue-600">80 minutes</span> of productivity<br />
          for every CSAM and CSA.
        </h1>
        <p class="text-3xl font-semibold text-slate-600 mt-5">
          Answer your follow-ups by voice while you drive. Cowork does the rest.
        </p>
      </div>

      <div class="grid grid-cols-1 lg:grid-cols-2 gap-8 items-start">
      <div class="space-y-8">
      <!-- 1. Input -->
      <form
        id="call-form"
        data-testid="call-form"
        :class="[card, 'space-y-4']"
        @submit.prevent="placeCall"
      >
        <div class="grid grid-cols-[auto_1fr_auto] gap-2 items-end">
          <div>
            <label for="country-code" :class="heading">Country code</label>
            <div
              class="mt-2 flex items-center px-3 py-2.5 rounded-lg border border-slate-300 bg-white tabular-nums"
            >
              +<input
                id="country-code"
                v-model="countryCode"
                name="countryCode"
                data-testid="country-code"
                aria-label="Country code"
                inputmode="numeric"
                class="w-10 bg-transparent focus:outline-none"
                :disabled="isBusy"
              />
            </div>
          </div>
          <div>
            <label for="phone-number" :class="heading">Phone number</label>
            <input
              id="phone-number"
              v-model="phone"
              name="phoneNumber"
              data-testid="phone-number"
              type="tel"
              inputmode="tel"
              autocomplete="tel-national"
              placeholder="12 34 56 78"
              aria-label="Phone number"
              :class="[field, 'mt-2 tabular-nums']"
              :disabled="isBusy"
            />
          </div>
          <div>
            <label for="call-language" :class="heading">Language</label>
            <select
              id="call-language"
              v-model="lang"
              name="language"
              data-testid="call-language"
              aria-label="Call language"
              :class="[field, 'mt-2']"
              :disabled="isBusy"
            >
              <option v-for="(l, code) in LANGS" :key="code" :value="code">
                {{ l.label }}
              </option>
            </select>
          </div>
        </div>

        <div>
          <div class="flex items-center justify-between">
            <label for="system-instructions" :class="heading"
              >System instructions</label
            >
            <div class="flex items-center gap-4">
              <button
                id="insert-example"
                type="button"
                data-testid="insert-example"
                class="text-lg font-semibold text-blue-600 hover:underline disabled:opacity-40"
                :disabled="isBusy"
                @click="instructions = exampleFor(lang)"
              >
                Load example
              </button>
              <button
                id="insert-template"
                type="button"
                data-testid="insert-template"
                class="text-lg font-semibold text-blue-600 hover:underline disabled:opacity-40"
                :disabled="isBusy"
                @click="instructions = templateFor(lang)"
              >
                Insert template
              </button>
            </div>
          </div>
          <textarea
            id="system-instructions"
            v-model="instructions"
            name="systemInstructions"
            data-testid="system-instructions"
            aria-label="System instructions"
            rows="6"
            placeholder="What should the agent ask about? List today's open questions, replies and reports."
            :class="[field, 'mt-2 text-base leading-relaxed']"
            :disabled="isBusy"
          />
        </div>

        <div class="flex flex-wrap items-center gap-4">
          <button
            id="place-call"
            type="submit"
            data-testid="place-call"
            class="px-14 py-5 rounded-xl bg-blue-600 text-white text-3xl font-extrabold hover:bg-blue-700 disabled:opacity-40 disabled:cursor-not-allowed"
            :disabled="!canCall"
          >
            Call me
          </button>
          <button
            id="copy-cowork-prompt"
            type="button"
            data-testid="copy-cowork-prompt"
            :data-copied="copied ? 'true' : 'false'"
            class="px-6 py-4 rounded-xl border border-slate-300 bg-white text-slate-700 text-xl font-semibold hover:bg-slate-50"
            @click="copyCoworkPrompt"
          >
            {{ copied ? "Copied" : "Copy Cowork prompt" }}
          </button>
          <button
            v-if="contextId && !isBusy"
            id="new-call"
            type="button"
            data-testid="new-call"
            class="px-6 py-4 rounded-xl border border-slate-300 bg-white text-slate-700 text-xl font-semibold hover:bg-slate-50"
            @click="resetForm"
          >
            New call
          </button>
        </div>
      </form>

      <!-- 2. Status -->
      <section :class="[card, 'space-y-2']" aria-labelledby="status-heading">
        <h2 id="status-heading" :class="heading">Status</h2>
        <p
          id="call-status"
          data-testid="call-status"
          :data-status="status"
          role="status"
          aria-live="polite"
          :class="[
            'inline-block px-6 py-3 rounded-full text-3xl font-bold ring-1',
            statusTone,
          ]"
        >
          {{ statusLabel[status] }}
        </p>
        <p v-if="contextId" class="text-xs text-slate-500">
          Call id:
          <span id="context-id" data-testid="context-id" class="tabular-nums">{{
            contextId
          }}</span>
        </p>
        <p
          v-if="errorMessage"
          id="call-error"
          data-testid="call-error"
          role="alert"
          class="text-sm text-red-700"
        >
          {{ errorMessage }}
        </p>
        <p
          v-if="isDone"
          id="call-done"
          data-testid="call-done"
          class="text-xl font-bold text-green-700"
        >
          The call is completed. Transcript and result are ready.
        </p>
      </section>
      </div>

      <div class="space-y-8">
      <section v-if="news.length || agenda.length" :class="card">
        <div v-if="news.length" data-testid="news" :data-news-count="news.length">
          <span :class="heading">Since last night</span>
          <ul class="mt-6 space-y-5 list-none">
            <li
              v-for="(n, i) in news"
              :key="i"
              data-testid="news-item"
              class="flex items-start gap-5 text-4xl leading-snug text-slate-700"
            >
              <span class="mt-4 h-3 w-3 rounded-full bg-slate-400 shrink-0" />
              <span>{{ headline(n) }}</span>
            </li>
          </ul>
        </div>

        <div v-if="agenda.length" class="mt-12" data-testid="agenda" :data-agenda-count="agenda.length">
          <span :class="heading">Today's plan</span>
          <ol class="mt-6 space-y-6 list-none">
            <li
              v-for="item in agenda"
              :key="item.n"
              data-testid="agenda-item"
              :data-priority="item.priority"
              :data-urgent="item.urgent ? 'true' : 'false'"
              class="flex items-start gap-5"
            >
              <span
                :class="['mt-2 h-8 w-8 rounded-full shrink-0', statusOf(item).dot]"
                :title="statusOf(item).label"
              />
              <span class="flex-1">
                <span :class="['block text-xl font-bold uppercase tracking-wider', statusOf(item).text]">{{
                  statusOf(item).label
                }}</span>
                <span class="block text-4xl leading-snug font-semibold text-slate-800">{{ headline(item.text) }}</span>
              </span>
            </li>
          </ol>
        </div>

      </section>

      <!-- 3. Transcript -->
      <section :class="[card, 'space-y-3']" aria-labelledby="transcript-heading">
        <h2 id="transcript-heading" :class="heading">Transcript</h2>
        <p
          v-if="!snapshot?.entries.length"
          id="transcript-empty"
          data-testid="transcript-empty"
          class="text-sm text-slate-500"
        >
          No transcript yet.
        </p>
        <ol
          v-else
          id="transcript"
          data-testid="transcript"
          aria-label="Transcript"
          class="space-y-2"
        >
          <li
            v-for="(e, i) in snapshot.entries"
            :key="i"
            :data-speaker="e.speaker"
            class="text-2xl leading-relaxed"
          >
            <span
              :class="[
                'font-semibold',
                e.speaker === 'ai' ? 'text-blue-700' : 'text-slate-700',
              ]"
              >{{ speakerLabel(e.speaker) }}:</span
            >
            {{ e.text }}
          </li>
        </ol>
      </section>

      <!-- 4. Summary -->
      <section
        v-if="snapshot?.summary"
        id="call-summary"
        data-testid="call-summary"
        :class="[card, 'space-y-2']"
        aria-labelledby="summary-heading"
      >
        <h2 id="summary-heading" :class="heading">Summary</h2>
        <p class="text-2xl leading-relaxed">{{ snapshot.summary }}</p>
        <p class="text-xs text-slate-500">
          Outcome:
          <span data-testid="call-outcome">{{ snapshot.outcome }}</span>
          <template v-if="snapshot.topics?.length">
            · Topics: {{ snapshot.topics.join(", ") }}
          </template>
        </p>
        <p
          v-if="snapshot.followUpDraft"
          data-testid="follow-up-draft"
          class="text-sm leading-relaxed"
        >
          <span class="font-semibold">Suggested follow-up:</span>
          {{ snapshot.followUpDraft }}
        </p>
      </section>

      <!-- 5. Actions for Cowork to carry out (ESXP, Outlook, Teams) -->
      <section
        v-if="snapshot?.summary"
        id="call-actions"
        data-testid="call-actions"
        :data-action-count="snapshot.actions?.length ?? 0"
        :class="[card, 'space-y-3']"
        aria-labelledby="actions-heading"
      >
        <div class="flex items-center justify-between gap-3">
          <h2 id="actions-heading" :class="heading">Actions</h2>
          <span
            v-if="snapshot.actions?.length"
            class="text-5xl font-extrabold text-blue-700 tabular-nums"
            >{{ readyCount }} of {{ snapshot.actions.length }} solved</span
          >
        </div>
        <div
          v-if="snapshot.actions?.length"
          class="h-5 w-full rounded-full bg-slate-100 overflow-hidden"
          role="progressbar"
          :aria-valuenow="readyCount"
          aria-valuemin="0"
          :aria-valuemax="snapshot.actions.length"
            aria-label="Tasks solved"
        >
          <div
            class="h-full bg-blue-600"
            :style="{ width: `${(readyCount / snapshot.actions.length) * 100}%` }"
          />
        </div>
        <p
          v-if="!snapshot.actions?.length"
          data-testid="actions-empty"
          class="text-sm text-slate-500"
        >
          No actions were given on the call.
        </p>
        <template v-else>
            <div
              v-for="g in actionGroups"
              :key="g.key"
              :data-testid="`actions-${g.key}`"
              :data-count="g.items.length"
              class="space-y-2"
            >
              <h3 class="text-2xl font-bold text-slate-700">
                {{ g.label }}
                <span class="font-normal text-slate-500 tabular-nums">({{ g.items.length }})</span>
              </h3>
              <p v-if="!g.items.length" class="text-xs text-slate-400">None</p>
              <ul v-else class="space-y-2">
                <li
                  v-for="(a, i) in g.items"
                  :key="i"
                  data-testid="action"
                  :data-action-type="a.type"
                  :data-action-target="a.target"
                  :data-action-status="a.status"
                  :data-action-priority="a.priority ?? 'today'"
                  class="rounded-lg border border-slate-200 p-5 text-2xl"
                >
                  <div class="flex flex-wrap items-center gap-2">
                    <span class="font-semibold">{{ actionLabel(a.type) }}</span>
                    <span class="text-slate-500">·</span>
                    <span data-testid="action-target">{{ a.target }}</span>
                    <span
                      :class="[
                        'ml-auto rounded-full px-2 py-0.5 text-xs ring-1',
                        a.priority === 'later'
                          ? 'bg-slate-50 text-slate-600 ring-slate-200'
                          : 'bg-blue-50 text-blue-700 ring-blue-200',
                      ]"
                      >{{ a.priority === "later" ? "Can wait" : "Today" }}</span
                    >
                    <span :class="['rounded-full px-2 py-0.5 text-xs ring-1', g.badge]">{{
                      g.label
                    }}</span>
                  </div>
                  <p data-testid="action-content" class="mt-1 leading-relaxed">
                    {{ a.content }}
                  </p>
                </li>
              </ul>
            </div>
        </template>
      </section>

      </div>
      </div>

      <!-- 6. Machine-readable result -->
      <section
        v-if="snapshot"
        :class="[card, 'space-y-2']"
        aria-labelledby="json-heading"
      >
        <h2 id="json-heading" :class="heading">Result (JSON)</h2>
        <pre
          id="result-json"
          data-testid="result-json"
          aria-label="Result as JSON"
          class="text-xs leading-relaxed whitespace-pre-wrap break-words bg-slate-50 border border-slate-200 rounded-lg p-3"
          >{{ resultJson }}</pre
        >
      </section>
    </main>
  </div>
</template>
