<script setup lang="ts">
import {
    ANALYSIS_CATEGORIES,
    type AnalysisScores,
} from "~/composables/useCallAnalysis";
import type { CallSession } from "~/composables/useCallSessions";

const props = defineProps<{ session: CallSession }>();
const emit = defineEmits<{
  toggle: [];
  remove: [];
}>();

const transcriptScroller = ref<HTMLElement | null>(null);

watch(
  () => props.session.transcript.length,
  async () => {
    await nextTick();
    if (transcriptScroller.value) {
      transcriptScroller.value.scrollTop =
        transcriptScroller.value.scrollHeight;
    }
  },
);

function statusBadge(s: CallSession["status"]) {
  if (s === "ringing")
    return {
      text: "Ringer",
      classes: "bg-amber-50 text-amber-700",
      dot: "bg-amber-500 animate-pulse",
    };
  if (s === "live")
    return {
      text: "Live",
      classes: "bg-emerald-50 text-emerald-700",
      dot: "bg-emerald-500 animate-pulse",
    };
  return {
    text: "Afsluttet",
    classes: "bg-zinc-100 text-zinc-500",
    dot: "bg-zinc-400",
  };
}

function timeFmt(t: string) {
  try {
    return new Date(t).toLocaleTimeString([], {
      hour: "2-digit",
      minute: "2-digit",
    });
  } catch {
    return "";
  }
}

function speakerLabel(speaker: string) {
  if (speaker === "ai") return "Agent";
  if (speaker === "user") return "Kunde";
  return "System";
}

function displayValue(
  cat: { key: keyof AnalysisScores; inverted?: boolean },
  raw: number,
) {
  return cat.inverted ? 5 - raw : raw;
}

function barColor(cat: { color: string }) {
  // Norlys red accent for the strongest categories; everything else neutral
  return "bg-[#ED0812]";
}

const sentiment = computed(() => {
  const s = props.session.analysis;
  if (!s) return null;
  return s.OverallSentiment;
});

function sentimentDotColor(v: number | null) {
  if (v === null) return "bg-zinc-300";
  if (v >= 4) return "bg-emerald-500";
  if (v >= 3) return "bg-lime-500";
  if (v >= 2) return "bg-amber-500";
  return "bg-rose-500";
}

// ---------------------------------------------------------------------------
// "Headline" sentiment summary — derives a short status from the 15 scores.
// Priority: angry/frustrated > confused/unhappy > resolved/happy > neutral.
// ---------------------------------------------------------------------------
interface CallSummary {
  emoji: string;
  label: string;
  detail: string;
  tone: "good" | "warn" | "bad" | "neutral";
}

const summary = computed<CallSummary | null>(() => {
  const a = props.session.analysis;
  if (!a) return null;

  const frustration = a.Frustration ?? 0;
  const mood = a.CustomerMood ?? 3;
  const churn = a.ChurnRisk ?? 0;
  const resolved = a.ResolutionProgress ?? 0;
  const overall = a.OverallSentiment ?? 3;
  const trust = a.TrustInAgent ?? 3;

  // 1. Angry / frustrated takes precedence
  if (frustration >= 4 || mood <= 1) {
    return {
      emoji: "😡",
      label: "Frustreret kunde",
      detail: "Kunden virker vred — overvej eskalering",
      tone: "bad",
    };
  }
  // 2. At-risk
  if (churn >= 4 || (frustration >= 3 && resolved <= 2)) {
    return {
      emoji: "⚠️",
      label: "Kunde i risiko",
      detail: "Høj churn-risiko eller uløst problem",
      tone: "bad",
    };
  }
  // 3. Confused / unhappy
  if (mood <= 2 || trust <= 2) {
    return {
      emoji: "😕",
      label: "Utilfreds",
      detail: "Lav stemning — agenten skal være ekstra empatisk",
      tone: "warn",
    };
  }
  // 4. Resolved / happy
  if (resolved >= 4 && overall >= 4) {
    return {
      emoji: "🎉",
      label: "Problem løst",
      detail: "Kunden fik den hjælp de havde brug for",
      tone: "good",
    };
  }
  if (overall >= 4 && mood >= 4) {
    return {
      emoji: "😊",
      label: "Tilfreds kunde",
      detail: "Positiv samtale i god gænge",
      tone: "good",
    };
  }
  // 5. Neutral
  return {
    emoji: "🙂",
    label: "Samtale i gang",
    detail: "Neutral stemning — intet alarmerende",
    tone: "neutral",
  };
});

function summaryClasses(tone: CallSummary["tone"]) {
  switch (tone) {
    case "good":
      return {
        bg: "bg-emerald-50",
        border: "border-emerald-200",
        text: "text-emerald-800",
        sub: "text-emerald-600",
      };
    case "warn":
      return {
        bg: "bg-amber-50",
        border: "border-amber-200",
        text: "text-amber-800",
        sub: "text-amber-600",
      };
    case "bad":
      return {
        bg: "bg-rose-50",
        border: "border-rose-200",
        text: "text-rose-800",
        sub: "text-rose-600",
      };
    default:
      return {
        bg: "bg-zinc-50",
        border: "border-zinc-200",
        text: "text-zinc-700",
        sub: "text-zinc-500",
      };
  }
}
</script>

<template>
  <article
    class="border border-zinc-200 rounded-xl overflow-hidden bg-white shadow-sm"
  >
    <!-- Header (always visible) -->
    <button
      type="button"
      class="w-full flex items-center gap-3 px-4 py-3 hover:bg-zinc-50 text-left transition-colors"
      @click="emit('toggle')"
    >
      <!-- Direction icon -->
      <div
        class="shrink-0 w-9 h-9 rounded-full flex items-center justify-center"
        :class="
          session.direction === 'outbound'
            ? 'bg-red-50 text-[#ED0812]'
            : 'bg-emerald-50 text-emerald-600'
        "
      >
        <svg
          xmlns="http://www.w3.org/2000/svg"
          class="w-4 h-4"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
        >
          <path
            d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
          />
        </svg>
      </div>

      <div class="flex-1 min-w-0">
        <div class="flex items-center gap-2">
          <span class="text-sm font-semibold text-zinc-900 truncate">{{
            session.customerName
          }}</span>
          <span class="text-xs text-zinc-400 font-mono shrink-0">{{
            session.phoneNumber
          }}</span>
        </div>
        <div class="flex items-center gap-2 mt-0.5">
          <span class="text-[11px] text-zinc-500 truncate">{{
            session.personaLabel
          }}</span>
          <span class="text-[11px] text-zinc-300">•</span>
          <span class="text-[11px] text-zinc-400">{{
            timeFmt(session.startedAt)
          }}</span>
        </div>
      </div>

      <!-- Live sentiment summary chip (compact, in header) -->
      <span
        v-if="summary"
        class="shrink-0 hidden sm:inline-flex items-center gap-1 text-[10px] font-semibold px-2 py-0.5 rounded-full border"
        :class="[
          summaryClasses(summary.tone).bg,
          summaryClasses(summary.tone).border,
          summaryClasses(summary.tone).text,
        ]"
        :title="summary.detail"
      >
        <span class="text-xs leading-none">{{ summary.emoji }}</span>
        {{ summary.label }}
      </span>

      <!-- Live sentiment dot (mobile-only — chip is hidden) -->
      <span
        v-if="sentiment !== null"
        class="shrink-0 sm:hidden w-2.5 h-2.5 rounded-full"
        :class="sentimentDotColor(sentiment)"
        :title="`Overall sentiment: ${sentiment}/5`"
      />

      <!-- Status badge -->
      <span
        class="shrink-0 inline-flex items-center gap-1.5 text-[10px] font-medium px-2 py-0.5 rounded-full"
        :class="statusBadge(session.status).classes"
      >
        <span
          class="w-1.5 h-1.5 rounded-full"
          :class="statusBadge(session.status).dot"
        />
        {{ statusBadge(session.status).text }}
      </span>

      <!-- Chevron -->
      <svg
        xmlns="http://www.w3.org/2000/svg"
        class="shrink-0 w-4 h-4 text-zinc-400 transition-transform"
        :class="session.expanded ? 'rotate-180' : ''"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
      >
        <polyline points="6 9 12 15 18 9" />
      </svg>
    </button>

    <!-- Expanded body -->
    <div v-if="session.expanded" class="border-t border-zinc-100">
      <!-- Sentiment headline banner -->
      <div
        v-if="summary"
        class="px-4 py-3 border-b flex items-center gap-3 transition-colors"
        :class="[
          summaryClasses(summary.tone).bg,
          summaryClasses(summary.tone).border,
        ]"
      >
        <span class="text-2xl leading-none">{{ summary.emoji }}</span>
        <div class="flex-1 min-w-0">
          <div
            class="text-sm font-bold leading-tight"
            :class="summaryClasses(summary.tone).text"
          >
            {{ summary.label }}
          </div>
          <div
            class="text-[11px] leading-snug mt-0.5"
            :class="summaryClasses(summary.tone).sub"
          >
            {{ summary.detail }}
          </div>
        </div>
        <!-- Mini stat triple — sentiment / mood / resolution -->
        <div
          v-if="session.analysis"
          class="hidden sm:flex items-center gap-3 text-[10px] uppercase tracking-wide"
          :class="summaryClasses(summary.tone).sub"
        >
          <div class="flex flex-col items-center">
            <span
              class="text-base font-bold leading-none"
              :class="summaryClasses(summary.tone).text"
              >{{ session.analysis.OverallSentiment }}</span
            >
            <span>Stemning</span>
          </div>
          <div class="flex flex-col items-center">
            <span
              class="text-base font-bold leading-none"
              :class="summaryClasses(summary.tone).text"
              >{{ session.analysis.CustomerMood }}</span
            >
            <span>Humør</span>
          </div>
          <div class="flex flex-col items-center">
            <span
              class="text-base font-bold leading-none"
              :class="summaryClasses(summary.tone).text"
              >{{ session.analysis.ResolutionProgress }}</span
            >
            <span>Løsning</span>
          </div>
        </div>
      </div>

      <div
        class="grid grid-cols-1 lg:grid-cols-2 divide-y lg:divide-y-0 lg:divide-x divide-zinc-100"
      >
        <!-- Transcript -->
        <div class="flex flex-col h-72">
          <div
            class="px-3 py-2 text-[10px] font-semibold text-zinc-400 uppercase tracking-wide border-b border-zinc-50"
          >
            Transskription
          </div>
          <div
            ref="transcriptScroller"
            class="flex-1 overflow-y-auto px-3 py-2 space-y-1.5"
          >
            <div
              v-if="session.transcript.length === 0"
              class="text-xs text-zinc-400 italic text-center py-6"
            >
              {{
                session.status === "ringing"
                  ? "Venter på forbindelse…"
                  : "Ingen tale endnu."
              }}
            </div>
            <div
              v-for="(e, idx) in session.transcript"
              :key="idx"
              class="text-xs"
            >
              <div class="flex items-center gap-1.5">
                <span
                  class="font-semibold"
                  :class="
                    e.speaker === 'ai'
                      ? 'text-[#ED0812]'
                      : e.speaker === 'user'
                        ? 'text-zinc-700'
                        : 'text-zinc-400'
                  "
                  >{{ speakerLabel(e.speaker) }}</span
                >
                <span class="text-[10px] text-zinc-400">{{
                  timeFmt(e.timestamp)
                }}</span>
              </div>
              <div class="text-zinc-700 leading-snug pl-0.5">{{ e.text }}</div>
            </div>
          </div>
        </div>

        <!-- Analysis -->
        <div class="flex flex-col h-72">
          <div
            class="px-3 py-2 text-[10px] font-semibold text-zinc-400 uppercase tracking-wide border-b border-zinc-50 flex items-center justify-between"
          >
            <span>Live analyse</span>
            <span
              v-if="session.analysis"
              class="text-zinc-300 normal-case font-normal"
            >
              {{ session.analysisUpdates }} opdateringer
            </span>
          </div>
          <div class="flex-1 overflow-y-auto px-3 py-2 space-y-1.5">
            <div
              v-if="!session.analysis"
              class="text-xs text-zinc-400 italic text-center py-6"
            >
              Venter på analyse…
            </div>
            <template v-else>
              <div
                v-for="cat in ANALYSIS_CATEGORIES"
                :key="cat.key"
                class="flex items-center gap-2"
              >
                <span class="text-[10px]">{{ cat.icon }}</span>
                <span class="text-[11px] text-zinc-600 w-32 truncate">{{
                  cat.label
                }}</span>
                <div
                  class="flex-1 h-1.5 bg-zinc-100 rounded-full overflow-hidden"
                >
                  <div
                    :class="barColor(cat)"
                    class="h-full transition-all duration-500 ease-out"
                    :style="`width: ${(displayValue(cat, session.analysis[cat.key] as number) / 5) * 100}%`"
                  />
                </div>
                <span
                  class="text-[10px] text-zinc-500 font-mono w-6 text-right"
                >
                  {{ displayValue(cat, session.analysis[cat.key] as number) }}/5
                </span>
              </div>
            </template>
          </div>
        </div>
      </div>

      <!-- Footer actions -->
      <div
        class="flex items-center justify-end gap-2 px-3 py-2 border-t border-zinc-100 bg-zinc-50/50"
      >
        <button
          v-if="session.status === 'ended'"
          type="button"
          class="text-[11px] text-zinc-500 hover:text-[#ED0812] px-2 py-1 transition-colors"
          @click="emit('remove')"
        >
          Fjern fra liste
        </button>
      </div>
    </div>
  </article>
</template>
