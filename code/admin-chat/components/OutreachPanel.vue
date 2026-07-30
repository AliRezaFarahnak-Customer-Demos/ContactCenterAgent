<script setup lang="ts">
import type {
    CustomerSummary,
    OutreachResult,
} from "~/composables/useOutreach";

const props = defineProps<{ expanded?: boolean }>();

const {
  channels,
  customers,
  mcpUrl,
  loadChannels,
  loadCustomers,
  loadTimeline,
  loadMcpUrl,
  startOutreach,
  simulateReply,
  sendFollowUp,
  clearAll,
} = useOutreach();

const channel = ref<"voice" | "sms" | "email">("sms");
const name = ref("");
const phone = ref("");
const email = ref("");
const message = ref("");
const sending = ref(false);
const sendError = ref<string | null>(null);
const lastSent = ref<OutreachResult | null>(null);

const expanded = ref<string | null>(null);
const timelines = reactive<Record<string, OutreachResult[]>>({});
const mcpCopied = ref(false);
const replyText = reactive<Record<string, string>>({});
const replying = ref<string | null>(null);

const channelMeta: Record<string, { label: string; dot: string }> = {
  voice: { label: "Opkald", dot: "bg-norlys-petroleum" },
  sms: { label: "SMS", dot: "bg-norlys-red" },
  email: { label: "E-mail", dot: "bg-norlys-petroleum-3" },
};

const activeCap = computed(() =>
  channels.value.find((c) => c.channel === channel.value),
);

const canSend = computed(() => {
  if (sending.value) return false;
  if (channel.value === "email") return !!email.value.trim();
  return !!phone.value.trim();
});

async function refresh() {
  await loadCustomers();
  // Refresh any open timeline so inbound replies show up live.
  if (expanded.value)
    timelines[expanded.value] = await loadTimeline(expanded.value);
}

async function toggle(customerId: string) {
  if (expanded.value === customerId) {
    expanded.value = null;
    return;
  }
  expanded.value = customerId;
  timelines[customerId] = await loadTimeline(customerId);
}

async function send() {
  sendError.value = null;
  sending.value = true;
  try {
    const result = await startOutreach({
      channel: channel.value,
      name: name.value.trim() || undefined,
      phone: phone.value.trim() || undefined,
      email: email.value.trim() || undefined,
      message: message.value.trim() || undefined,
    });
    lastSent.value = result;
    if (result.status === "failed") {
      sendError.value = result.reply || "Kunne ikke sende.";
    } else {
      message.value = "";
    }
    await refresh();
  } catch (err: unknown) {
    sendError.value = err instanceof Error ? err.message : String(err);
  } finally {
    sending.value = false;
  }
}

// Demo aid: post a simulated customer reply so the two-way thread renders without a live carrier inbound.
async function sendReply(c: CustomerSummary) {
  const text = (replyText[c.customerId] || "").trim();
  const from = c.phone || c.email;
  if (!text || !from) return;
  replying.value = c.customerId;
  try {
    await simulateReply({
      from,
      message: text,
      channel: c.phone ? "sms" : "email",
    });
    replyText[c.customerId] = "";
    timelines[c.customerId] = await loadTimeline(c.customerId);
    await loadCustomers();
  } finally {
    replying.value = null;
  }
}

const followUpText = reactive<Record<string, string>>({});
const followingUp = ref<string | null>(null);

// Real outbound follow-up from the agent on the customer's latest thread.
async function sendFollowUpTo(c: CustomerSummary) {
  const text = (followUpText[c.customerId] || "").trim();
  const recs = timelines[c.customerId] || [];
  const latest = recs[recs.length - 1];
  if (!text || !latest) return;
  followingUp.value = c.customerId;
  try {
    if (await sendFollowUp(latest.outreachId, text)) {
      followUpText[c.customerId] = "";
      timelines[c.customerId] = await loadTimeline(c.customerId);
      await loadCustomers();
    }
  } finally {
    followingUp.value = null;
  }
}

// Sentiment metrics worth surfacing in the timeline (0-6 scale, 3 = neutral).
const metricLabels: Record<string, string> = {
  satisfaction: "Tilfredshed",
  problem_solved: "Problem løst",
  churn_risk: "Churn-risiko",
};
function metricChips(rec: OutreachResult): { label: string; value: string }[] {
  if (!rec.metrics) return [];
  return Object.entries(metricLabels)
    .filter(([k]) => rec.metrics![k] !== undefined)
    .map(([k, label]) => ({ label, value: `${rec.metrics![k]}/6` }));
}

function copyMcp() {
  if (!mcpUrl.value) return;
  navigator.clipboard?.writeText(mcpUrl.value);
  mcpCopied.value = true;
  setTimeout(() => (mcpCopied.value = false), 1500);
}

const clearing = ref(false);
const confirmClear = ref(false);

async function doClearAll() {
  if (!confirmClear.value) {
    confirmClear.value = true;
    setTimeout(() => (confirmClear.value = false), 4000);
    return;
  }
  clearing.value = true;
  try {
    await clearAll();
    expanded.value = null;
    lastSent.value = null;
    confirmClear.value = false;
    await loadCustomers();
  } finally {
    clearing.value = false;
  }
}

function fmtTime(iso: string) {
  try {
    return new Date(iso).toLocaleString("da-DK", {
      day: "2-digit",
      month: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
    });
  } catch {
    return iso;
  }
}

const statusText: Record<string, string> = {
  pending: "afventer",
  awaiting_reply: "venter på svar",
  completed: "afsluttet",
  failed: "fejlede",
  no_answer: "intet svar",
};

let timer: ReturnType<typeof setInterval> | undefined;
onMounted(async () => {
  await Promise.all([loadChannels(), loadCustomers(), loadMcpUrl()]);
  timer = setInterval(refresh, 8000);
});
onBeforeUnmount(() => timer && clearInterval(timer));
</script>

<template>
  <aside
    class="flex flex-col min-h-0 bg-white rounded-xl overflow-hidden"
    :class="
      props.expanded
        ? 'flex-1 min-w-0'
        : 'flex-1 min-w-0 lg:hidden xl:flex xl:flex-none xl:w-[26rem] 2xl:w-[32rem]'
    "
  >
    <div
      class="px-4 py-3 flex items-center justify-between border-b border-norlys-sand"
    >
      <div class="flex items-center gap-2">
        <span class="font-headline text-base font-bold text-norlys-petroleum-3"
          >Kundesessioner</span
        >
        <span
          class="inline-flex items-center justify-center min-w-[20px] h-5 px-1.5 rounded-full bg-norlys-red text-norlys-sand text-[10px] font-bold"
          >{{ customers.length }}</span
        >
      </div>
      <div class="flex items-center gap-3">
        <button
          v-if="customers.length"
          type="button"
          class="text-[11px] transition-colors"
          :class="
            confirmClear
              ? 'text-norlys-red font-semibold'
              : 'text-norlys-petroleum hover:text-norlys-red'
          "
          :disabled="clearing"
          @click="doClearAll()"
        >
          {{
            clearing ? "Rydder…" : confirmClear ? "Bekræft: slet alle" : "Slet alle"
          }}
        </button>
        <button
          type="button"
          class="text-[11px] text-norlys-petroleum hover:text-norlys-red transition-colors"
          @click="refresh()"
        >
          Opdater
        </button>
      </div>
    </div>

    <div class="flex-1 overflow-y-auto">
      <!-- ===== Composer ===== -->
      <div class="p-3 border-b border-norlys-sand space-y-2.5">
        <div class="flex gap-1.5">
          <button
            v-for="ch in ['voice', 'sms', 'email'] as const"
            :key="ch"
            type="button"
            class="flex-1 flex items-center justify-center gap-1.5 py-1.5 rounded-lg text-xs font-medium transition-colors"
            :class="
              channel === ch
                ? 'bg-norlys-petroleum text-norlys-sand'
                : 'bg-norlys-sand text-norlys-petroleum hover:bg-norlys-light-petroleum/30'
            "
            @click="channel = ch"
          >
            <span
              class="w-1.5 h-1.5 rounded-full"
              :class="channelMeta[ch].dot"
            />
            {{ channelMeta[ch].label }}
          </button>
        </div>

        <input
          v-model="name"
          type="text"
          placeholder="Navn (valgfrit)"
          class="w-full px-3 py-2 rounded-lg bg-norlys-sand text-sm text-norlys-ink placeholder:text-norlys-petroleum/50 focus:outline-none focus:ring-2 focus:ring-norlys-petroleum/30"
        />
        <input
          v-if="channel !== 'email'"
          v-model="phone"
          type="tel"
          placeholder="Telefonnummer, fx +4512345678"
          class="w-full px-3 py-2 rounded-lg bg-norlys-sand text-sm text-norlys-ink placeholder:text-norlys-petroleum/50 focus:outline-none focus:ring-2 focus:ring-norlys-petroleum/30"
        />
        <input
          v-else
          v-model="email"
          type="email"
          placeholder="E-mailadresse"
          class="w-full px-3 py-2 rounded-lg bg-norlys-sand text-sm text-norlys-ink placeholder:text-norlys-petroleum/50 focus:outline-none focus:ring-2 focus:ring-norlys-petroleum/30"
        />
        <textarea
          v-model="message"
          rows="2"
          :placeholder="
            channel === 'voice'
              ? 'Formål / system-prompt (valgfrit)'
              : 'Besked til kunden'
          "
          class="w-full px-3 py-2 rounded-lg bg-norlys-sand text-sm text-norlys-ink placeholder:text-norlys-petroleum/50 focus:outline-none focus:ring-2 focus:ring-norlys-petroleum/30 resize-none"
        />

        <p
          v-if="activeCap && !activeCap.enabled"
          class="text-[11px] text-norlys-red"
        >
          {{ channelMeta[channel].label }} er ikke konfigureret.
          {{ activeCap.notes }}
        </p>
        <p v-else-if="activeCap" class="text-[11px] text-norlys-petroleum/70">
          {{ activeCap.notes }}
        </p>

        <button
          type="button"
          :disabled="!canSend"
          class="w-full py-2 rounded-lg bg-norlys-red text-norlys-sand text-sm font-semibold hover:bg-norlys-red/90 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
          @click="send()"
        >
          {{ sending ? "Sender…" : "Send udgående" }}
        </button>

        <p v-if="sendError" class="text-[11px] text-norlys-red">
          {{ sendError }}
        </p>
        <p
          v-else-if="lastSent && lastSent.status !== 'failed'"
          class="text-[11px] text-norlys-petroleum"
        >
          Sendt via
          {{ channelMeta[lastSent.channel]?.label ?? lastSent.channel }} —
          {{ statusText[lastSent.status] ?? lastSent.status }}.
        </p>
      </div>

      <!-- ===== Customer list ===== -->
      <div class="p-3 space-y-2">
        <div
          v-if="customers.length === 0"
          class="text-center text-norlys-petroleum/60 text-xs py-8 px-4"
        >
          Ingen kundesessioner endnu. Send en besked ovenfor, eller start et
          opkald.
        </div>

        <div
          v-for="c in customers"
          :key="c.customerId"
          class="rounded-lg bg-norlys-sand/60 overflow-hidden"
        >
          <button
            type="button"
            class="w-full px-3 py-2.5 flex items-center justify-between text-left hover:bg-norlys-sand transition-colors"
            @click="toggle(c.customerId)"
          >
            <div class="min-w-0">
              <div class="text-sm font-medium text-norlys-ink truncate">
                {{ c.name || c.phone || c.email || c.customerId }}
              </div>
              <div class="flex items-center gap-1.5 mt-1">
                <span
                  v-for="ch in c.channels"
                  :key="ch"
                  class="inline-flex items-center gap-1 text-[10px] text-norlys-petroleum/80"
                >
                  <span
                    class="w-1.5 h-1.5 rounded-full"
                    :class="channelMeta[ch]?.dot ?? 'bg-norlys-petroleum'"
                  />
                  {{ channelMeta[ch]?.label ?? ch }}
                </span>
              </div>
            </div>
            <div class="text-right shrink-0 ml-2">
              <div class="text-[10px] text-norlys-petroleum/60 tabular-nums">
                {{ fmtTime(c.lastActivity) }}
              </div>
              <div
                v-if="c.lastOutcome"
                class="text-[10px] font-medium text-norlys-petroleum-3 mt-0.5"
              >
                {{ c.lastOutcome }}
              </div>
            </div>
          </button>

          <!-- Timeline -->
          <div v-if="expanded === c.customerId" class="px-3 pb-3 space-y-2">
            <div
              v-for="rec in timelines[c.customerId] || []"
              :key="rec.outreachId"
              class="pl-3 border-l-2 border-norlys-light-petroleum/50 space-y-1.5"
            >
              <div
                v-for="(it, i) in rec.interactions"
                :key="i"
                class="text-[12px]"
              >
                <div
                  class="flex items-center gap-1.5 text-[10px] text-norlys-petroleum/60"
                >
                  <span
                    class="w-1.5 h-1.5 rounded-full"
                    :class="
                      channelMeta[it.channel]?.dot ?? 'bg-norlys-petroleum'
                    "
                  />
                  {{ channelMeta[it.channel]?.label ?? it.channel }} ·
                  {{ it.direction === "inbound" ? "kunde" : "Norlys" }} ·
                  {{ fmtTime(it.timestampUtc) }}
                </div>
                <div
                  class="rounded-lg px-2.5 py-1.5 text-norlys-ink whitespace-pre-wrap"
                  :class="
                    it.direction === 'inbound'
                      ? 'bg-white'
                      : 'bg-norlys-petroleum/10'
                  "
                >
                  {{ it.text }}
                </div>
              </div>
              <div
                v-if="rec.summary"
                class="text-[11px] text-norlys-petroleum/80 italic"
              >
                {{ rec.summary }}
              </div>
              <div
                v-if="metricChips(rec).length"
                class="flex flex-wrap gap-1"
              >
                <span
                  v-for="m in metricChips(rec)"
                  :key="m.label"
                  class="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-white text-[10px] text-norlys-petroleum-3 tabular-nums"
                >
                  {{ m.label }} {{ m.value }}
                </span>
              </div>
            </div>

            <!-- Send a real follow-up to the customer on their latest thread. -->
            <div
              v-if="(timelines[c.customerId] || []).length"
              class="flex gap-1.5 pt-1"
            >
              <input
                v-model="followUpText[c.customerId]"
                type="text"
                placeholder="Send opfølgning til kunden…"
                class="flex-1 px-2.5 py-1.5 rounded-lg bg-white text-[12px] text-norlys-ink placeholder:text-norlys-petroleum/50 focus:outline-none focus:ring-2 focus:ring-norlys-petroleum/30"
                @keyup.enter="sendFollowUpTo(c)"
              />
              <button
                type="button"
                :disabled="
                  followingUp === c.customerId ||
                  !(followUpText[c.customerId] || '').trim()
                "
                class="px-3 py-1.5 rounded-lg bg-norlys-red text-norlys-sand text-[11px] font-semibold hover:bg-norlys-red/90 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                @click="sendFollowUpTo(c)"
              >
                {{ followingUp === c.customerId ? "…" : "Send" }}
              </button>
            </div>

            <!-- Simulate a customer reply so the two-way thread shows (demo aid). -->
            <div v-if="c.phone || c.email" class="flex gap-1.5 pt-1">
              <input
                v-model="replyText[c.customerId]"
                type="text"
                placeholder="Simulér kundesvar…"
                class="flex-1 px-2.5 py-1.5 rounded-lg bg-white text-[12px] text-norlys-ink placeholder:text-norlys-petroleum/50 focus:outline-none focus:ring-2 focus:ring-norlys-petroleum/30"
                @keyup.enter="sendReply(c)"
              />
              <button
                type="button"
                :disabled="
                  replying === c.customerId ||
                  !(replyText[c.customerId] || '').trim()
                "
                class="px-3 py-1.5 rounded-lg bg-norlys-petroleum text-norlys-sand text-[11px] font-semibold hover:bg-norlys-petroleum/90 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                @click="sendReply(c)"
              >
                {{ replying === c.customerId ? "…" : "Svar" }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- ===== MCP server (agent access) — exposed URL + tools ===== -->
    <div
      v-if="mcpUrl"
      class="px-3 py-2.5 border-t border-norlys-sand bg-norlys-sand/40 space-y-1.5"
    >
      <div class="flex items-center justify-between">
        <span class="text-[11px] font-bold text-norlys-petroleum-3"
          >MCP-server (agent-adgang)</span
        >
        <button
          type="button"
          class="text-[10px] text-norlys-red hover:underline shrink-0"
          @click="copyMcp()"
        >
          {{ mcpCopied ? "Kopieret" : "Kopiér URL" }}
        </button>
      </div>
      <code
        class="block text-[10px] text-norlys-petroleum break-all leading-snug"
        >{{ mcpUrl }}</code
      >
      <div class="text-[10px] text-norlys-petroleum/60">
        Anonym · Streamable HTTP · værktøjer: start_outreach ·
        wait_for_outreach_result · get_outreach_result · send_followup ·
        list_outreach · list_customers · get_customer_timeline · list_channels ·
        list_personas
      </div>
    </div>
  </aside>
</template>
