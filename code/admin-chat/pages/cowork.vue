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
    intro: `Du er personlig assistent for Iben, der er Customer Success Account Manager hos Microsoft. Du ringer for at lave en kort daglig opfølgning. Tal dansk, kort og venligt. Stil kun ét spørgsmål ad gangen.

Navn: Iben`,
    driving: `HUN KØRER BIL
- Iben sidder ofte i bilen, når du ringer. Hun kan ikke se på en skærm eller skrive noget.
- Hold hver sætning kort og klar. Læs aldrig links, lange numre eller mailadresser op.
- Bed hende aldrig om at stave noget, kun aliaset.
- Hvis hun siger "vent", "øjeblik" eller at hun skal koncentrere sig om trafikken, så sig "selvfølgelig, jeg venter" og vær stille, til hun taler igen.
- Hvis hun siger at hun ikke kan tale sikkert lige nu, så sig at opgaverne venter, sig farvel og brug hang_up.
- Hun skal ikke gøre noget selv. Cowork skriver svarene og lukker rapporterne bagefter, og hun godkender det, når hun er fremme.`,
    opening: (hook) => `ÅBNING
- Hils kort: "Hej CustomerName, det er din assistent med dagens opfølgning. Du skal ikke kigge på noget, jeg holder det kort.${hook} Har du fem minutter?"
- Hvis hun siger nej, så sig at opgaverne venter til i morgen, sig farvel og brug hang_up.
- Hvis hun siger ja, så gå til SIKKERHED. Nævn ingen opgaver før sikkerheden er godkendt.`,
    security: (alias, engagements) => `SIKKERHED, FØR OPGAVERNE
- Sig: "Godt. Først to sikkerhedsspørgsmål. Hvad er dit alias?"
- Korrekt alias: ${alias}. Godkend kun præcis de tegn, også når de siges hver for sig, for eksempel bogstav for bogstav og tal for tal.
- Spørg derefter: "Kan du nævne to af dine kommende engagementer?"
- Hendes kommende engagementer:
${engagements.map((e) => `  - ${e}`).join("\n")}
- Godkend når hun nævner begge. Det er nok at nævne kunden.
- EKSEMPEL PÅ KORREKT: det rigtige alias og begge kunder fra listen.
- EKSEMPEL PÅ FORKERT: et alias med ét forkert tegn, kun ét engagement, eller en kunde der ikke står på listen.
- Afslør aldrig de rigtige svar, og giv ingen hints.
- Hvis et svar er forkert, må hun prøve én gang mere. Er det stadig forkert, så sig at du desværre ikke kan gennemgå opgaverne i dag, sig farvel og brug hang_up.`,
    news: (items) => `SIDEN I GÅR AFTES
Lige efter sikkerheden: fortæl kort hvad der er sket siden i går aftes, én kort sætning pr. punkt. Spørg ikke om noget her.
${items.map((n) => `- ${n}`).join("\n")}
Sig derefter "så til dagens opgaver" og gå videre.`,
    rules: `FOR HVER OPGAVE
- Tag opgaverne i rækkefølge, dem der skal svares i dag først.
- Læs opgaven kort op og spørg hvad svaret eller beslutningen er.
- For en rapport: få fat i hvad der blev leveret, resultatet og eventuelle næste skridt, så teksten kan skrives direkte ind i rapporten.
- For et svar: få fat i hvad der skal skrives, og til hvem.
- Gentag svaret kort med dine egne ord og bekræft at du har forstået det rigtigt.
- Opgaver der skal svares i dag: bliv ved med at spørge venligt, til du har et klart svar. Hvis hun vil udskyde en af dem, så mind hende én gang om at den skal klares i dag. Vil hun stadig udskyde, så notér den som ikke løst og gå videre.
- Opgaver der kan vente: spørg om hun vil klare den nu eller en anden dag. Vælger hun en anden dag, så notér den som udskudt og gå videre.
- Hun må også selv nævne ekstra opgaver, for eksempel "skriv til Birkedal Foods at workshoppen den 1. oktober er bekræftet". Bekræft dem på samme måde.

AFSLUTNING
- Før du afslutter: er der stadig opgaver der skal svares i dag uden svar, så spørg om dem én gang til.
- Spørg derefter: "Har du andre hurtige opdateringer eller resultater fra dagens engagementer, som jeg skal skrive ind eller lukke?" Tag hver opdatering, gentag den kort og spørg "andet?", indtil hun siger nej.
- Opsummér kort hvad der er løst, hvad der er udskudt til en anden dag, og hvad der ikke er løst.
- Sig at Cowork klarer resten, og at hun kan godkende det, når hun er fremme.
- Sig tak, ønsk hende en god tur, og brug hang_up.`,
  },
  en: {
    intro: `You are the personal assistant of Iben, a Customer Success Account Manager at Microsoft. You are calling for a short end-of-day follow-up. Speak English, briefly and warmly. Ask only one question at a time.`,
    driving: `SHE IS DRIVING
- Iben is often in the car when you call. She cannot look at a screen or type anything.
- Keep every sentence short and clear. Never read out links, long numbers or email addresses.
- Never ask her to spell anything, only the alias.
- If she says "wait", "one moment" or that she needs to focus on the traffic, say "of course, I'll wait" and stay quiet until she speaks again.
- If she says she cannot talk safely right now, say the tasks will wait, say goodbye and use hang_up.
- She does not have to do anything herself. Cowork writes the replies and closes the reports afterwards, and she approves them when she has arrived.`,
    opening: (hook) => `OPENING
- Greet briefly: "Hi Iben, it's your assistant with today's follow-ups. You don't need to look at anything, I'll keep it short.${hook} Do you have five minutes?"
- If she says no, say the tasks will wait until tomorrow, say goodbye and use hang_up.
- If she says yes, go to SECURITY. Do not mention any task before security has passed.`,
    security: (alias, engagements) => `SECURITY, BEFORE THE TASKS
- Say: "Great. First two security questions. What is your alias?"
- Correct alias: ${alias}. Accept only exactly those characters, also when spoken one by one, letter by letter and digit by digit.
- Then ask: "Can you name two of your upcoming engagements?"
- Her upcoming engagements:
${engagements.map((e) => `  - ${e}`).join("\n")}
- Accept when she names both. Naming the customer is enough.
- EXAMPLE OF CORRECT: the right alias and both customers from the list.
- EXAMPLE OF WRONG: an alias with one wrong character, only one engagement, or a customer not on the list.
- Never reveal the right answers and give no hints.
- If an answer is wrong, she may try once more. If it is still wrong, say you unfortunately cannot go through the tasks today, say goodbye and use hang_up.`,
    news: (items) => `SINCE YESTERDAY EVENING
Right after security: briefly say what has happened since yesterday evening, one short sentence per point. Do not ask anything here.
${items.map((n) => `- ${n}`).join("\n")}
Then say "now to today's tasks" and move on.`,
    rules: `FOR EACH TASK
- Take the tasks in order, the ones that must be answered today first.
- Read the task briefly and ask what the answer or decision is.
- For a report: capture what was delivered, the outcome and any next steps, so the text can go straight into the report.
- For a reply: capture what to write and to whom.
- Briefly repeat the answer in your own words and confirm you understood it correctly.
- Tasks that must be answered today: keep asking kindly until you have a clear answer. If she wants to postpone one, remind her once that it has to be done today. If she still wants to postpone, note it as not solved and move on.
- Tasks that can wait: ask whether she wants to do it now or another day. If another day, note it as postponed and move on.
- She may also add tasks herself, for example "tell Birkedal Foods the workshop on the 1st of October is confirmed". Confirm those the same way.

CLOSING
- Before you finish: if any task that must be answered today still has no answer, ask about it once more.
- Then ask: "Any other quick updates or outcomes from today's engagements that I should write down or close?" Take each update, repeat it briefly and ask "anything else?" until she says no.
- Briefly summarize what is solved, what is postponed to another day, and what is not solved.
- Say that Cowork will take care of the rest, and that she can approve it when she has arrived.
- Say thank you, wish her a safe drive, and use hang_up.`,
  },
};

const BLANK_PLAN: Record<Lang, CallPlan> = {
  da: {
    hook: "",
    today: [
      "Luk leverancerapport: Kunde ... Spørg hvad der blev leveret i dag, og om rapporten kan lukkes.",
      "Svar i Teams: Fra ... om ... Spørg hvad svaret skal være.",
    ],
    later: ["Svar på mail: Fra ... om ... Spørg hvad svaret skal være."],
    news: ["..."],
    alias: "...",
    engagements: ["...", "..."],
  },
  en: {
    hook: "",
    today: [
      "Close delivery report: Customer ... Ask what was delivered today and whether the report can be closed.",
      "Reply in Teams: From ... about ... Ask what the reply should say.",
    ],
    later: ["Reply to email: From ... about ... Ask what the reply should say."],
    news: ["..."],
    alias: "...",
    engagements: ["...", "..."],
  },
};

// Fictional demo day for Iben (CSAM), built from five real kinds of request she gets.
// Customers and people are fictional stand-ins; the context is what Cowork would have found in Teams, Outlook and the support portal.
const EXAMPLE_PLAN: Record<Lang, CallPlan> = {
  da: {
    hook: " Jeg har lidt nyt fra i aftes og fem opgaver. Tre skal svares i dag, og den første haster.",
    today: [
      "HASTER. Supportsagen for Havnestad Pension er gået i stå. Den har stået stille i seks dage, og kunden har skrevet to gange. Spørg om Cowork skal bede support om at prioritere sagen højere eller eskalere den til vagthavende, og hvad kunden skal have at vide.",
      "Opret en anmodning for Solbakke Pension. Kunden vil have en gennemgang af deres Azure-landingszone før en revision i november. Spørg hvad anmodningen skal indeholde, hvilken type hjælp de skal have, og hvornår den skal være færdig.",
      "Bekræft om en CSA er ledig den 14. oktober til en workshop hos Solbakke Pension. Cowork har fundet at Mikkel Dahl er ledig om formiddagen, og Laura Kjær er ledig hele dagen. Spørg hvem der skal bookes, og hvad kunden skal have at vide.",
    ],
    later: [
      "Find en ressource til spørgsmål om Azure DevOps. En udvikler hos Solbakke Pension har spørgsmål om pipelines. Cowork har fundet Anna Berg, CSA med Azure DevOps som speciale, og en intern Teams-kanal for Azure DevOps. Spørg hvem kunden skal kobles på, eller om det skal tages en anden dag.",
      "Hvor er Havnestad Pension med Security Copilot? Kunden startede en pilot med tyve brugere i august, og sikkerhedsgennemgangen mangler stadig. Spørg om status, næste skridt, og hvem der skal have opdateringen.",
    ],
    news: [
      "Havnestad Pension skrev igen i aftes om supportsagen. De er bekymrede for deres go-live.",
      "Stormkyst Forsikring har sendt deltagerlisten til hackathonet. Der kommer tolv personer.",
      "Solbakke Pension har delt deres nuværende arkitekturdiagram i Teams.",
    ],
    alias: "xyz123, udtales x, y, z, et, to, tre",
    engagements: [
      "Birkedal Foods, workshop om AI-agenter den 1. oktober",
      "Stormkyst Forsikring, AI-hackathon den 8. oktober",
    ],
  },
  en: {
    hook: " I have a little news from last night and five tasks. Three must be answered today, and the first one is urgent.",
    today: [
      "URGENT. The support ticket for Havnestad Pension is stalled. It has not moved for six days, and the customer has written twice. Ask whether Cowork should ask support to raise its priority or escalate it to the duty manager, and what the customer should be told.",
      "Create a request for Solbakke Pension. The customer wants a review of their Azure landing zone before an audit in November. Ask what the request should contain, what kind of help they need, and when it must be done.",
      "Confirm whether a CSA is available on the 14th of October for a workshop at Solbakke Pension. Cowork found that Mikkel Dahl is free in the morning and Laura Kjær is free all day. Ask who should be booked and what the customer should be told.",
    ],
    later: [
      "Find a resource for questions about Azure DevOps. A developer at Solbakke Pension has questions about pipelines. Cowork found Anna Berg, a CSA specialised in Azure DevOps, and an internal Teams channel for Azure DevOps. Ask who the customer should be connected with, or whether it should be done another day.",
      "Where is Havnestad Pension on Security Copilot? The customer started a pilot with twenty users in August, and the security review is still missing. Ask for the status, next steps, and who should get the update.",
    ],
    news: [
      "Havnestad Pension wrote again last night about the support ticket. They are worried about their go-live.",
      "Stormkyst Insurance sent the attendee list for the hackathon. Twelve people are coming.",
      "Solbakke Pension shared their current architecture diagram in Teams.",
    ],
    alias: "xyz123, spoken as x, y, z, one, two, three",
    engagements: [
      "Birkedal Foods, AI agents workshop on the 1st of October",
      "Stormkyst Insurance, AI hackathon on the 8th of October",
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
        languageCode: lang.value,
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
const card = "bg-white rounded-xl border border-slate-200 p-5";
const heading = "block text-sm font-bold text-slate-500 uppercase tracking-wider";
const field =
  "w-full px-3 py-2.5 rounded-lg border border-slate-300 bg-white focus:outline-none focus:border-blue-500 focus:ring-2 focus:ring-blue-500/20 disabled:bg-slate-50 disabled:text-slate-500";
</script>

<template>
  <div
    id="cowork-page"
    data-testid="cowork-page"
    :data-call-status="status"
    :data-call-done="isDone ? 'true' : 'false'"
    :data-context-id="contextId || undefined"
    class="min-h-screen bg-slate-50 text-slate-900"
    :style="{
      '--font-body': SYSTEM_FONT,
      '--font-headline': SYSTEM_FONT,
      fontFamily: SYSTEM_FONT,
    }"
  >
    <header class="h-20 bg-white border-b border-slate-200">
      <div class="max-w-5xl mx-auto h-full px-6 flex items-center gap-4">
        <img :src="FAVICON" alt="" class="h-11 w-11" draggable="false" />
        <span class="text-3xl font-extrabold tracking-tight text-slate-900">{{ APP_NAME }}</span>
        <span class="ml-auto text-lg font-semibold text-slate-500">Cowork + voice agent</span>
      </div>
    </header>

    <main class="max-w-5xl mx-auto px-6 py-10 space-y-6">
      <div>
        <h1 class="text-6xl font-extrabold tracking-tight text-left leading-tight">
          <span class="text-blue-600">+80 minutes</span> of productivity.<br />
          Added to your daily commute.
        </h1>
        <p class="text-2xl font-semibold text-slate-600 mt-4">
          Answer your follow-ups by voice while you drive. Cowork does the rest.
        </p>
      </div>

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
                class="text-xs font-semibold text-blue-600 hover:underline disabled:opacity-40"
                :disabled="isBusy"
                @click="instructions = exampleFor(lang)"
              >
                Load example
              </button>
              <button
                id="insert-template"
                type="button"
                data-testid="insert-template"
                class="text-xs font-semibold text-blue-600 hover:underline disabled:opacity-40"
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
            rows="5"
            placeholder="What should the agent ask about? List today's open questions, replies and reports."
            :class="[field, 'mt-2 text-sm leading-relaxed']"
            :disabled="isBusy"
          />
        </div>

        <div v-if="news.length" data-testid="news" :data-news-count="news.length">
          <span :class="heading">Since last night</span>
          <ul class="mt-3 space-y-2">
            <li
              v-for="(n, i) in news"
              :key="i"
              data-testid="news-item"
              class="flex items-start gap-3 text-xl text-slate-700"
            >
              <span class="mt-2.5 h-2.5 w-2.5 rounded-full bg-amber-500 shrink-0" />
              <span>{{ headline(n) }}</span>
            </li>
          </ul>
        </div>

        <div v-if="agenda.length" data-testid="agenda" :data-agenda-count="agenda.length">
          <span :class="heading">Today's plan</span>
          <ol class="mt-3 space-y-3">
            <li
              v-for="item in agenda"
              :key="item.n"
              data-testid="agenda-item"
              :data-priority="item.priority"
              :data-urgent="item.urgent ? 'true' : 'false'"
              class="flex items-center gap-3 text-xl"
            >
              <span class="tabular-nums font-bold text-slate-400 w-7 shrink-0">{{ item.n }}.</span>
              <span
                :class="[
                  'shrink-0 rounded-full px-3 py-1 text-base font-semibold ring-1',
                  item.priority === 'today'
                    ? 'bg-blue-50 text-blue-700 ring-blue-200'
                    : 'bg-slate-50 text-slate-600 ring-slate-200',
                ]"
                >{{ item.priority === "today" ? "Today" : "Can wait" }}</span
              >
              <span
                v-if="item.urgent"
                class="shrink-0 rounded-full px-3 py-1 text-base font-semibold ring-1 bg-red-50 text-red-700 ring-red-200"
                >Urgent</span
              >
              <span class="font-semibold text-slate-800">{{ headline(item.text) }}</span>
            </li>
          </ol>
        </div>

        <div class="flex items-center gap-3">
          <button
            id="place-call"
            type="submit"
            data-testid="place-call"
            class="px-10 py-4 rounded-xl bg-blue-600 text-white text-2xl font-extrabold hover:bg-blue-700 disabled:opacity-40 disabled:cursor-not-allowed"
            :disabled="!canCall"
          >
            Call me
          </button>
          <button
            v-if="contextId && !isBusy"
            id="new-call"
            type="button"
            data-testid="new-call"
            class="px-4 py-2.5 rounded-lg border border-slate-300 bg-white text-slate-700 font-semibold hover:bg-slate-50"
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
            'inline-block px-5 py-2 rounded-full text-2xl font-bold ring-1',
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
            class="text-lg leading-relaxed"
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
        <p class="text-lg leading-relaxed">{{ snapshot.summary }}</p>
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
            class="text-3xl font-extrabold text-blue-700 tabular-nums"
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
              <h3 class="text-xl font-bold text-slate-700">
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
                  class="rounded-lg border border-slate-200 p-4 text-lg"
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
