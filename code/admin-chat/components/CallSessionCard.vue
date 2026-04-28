<script setup lang="ts">
import type { CallSession } from "~/composables/useCallSessions";

const props = defineProps<{ session: CallSession }>();
const emit = defineEmits<{
  toggle: [];
  remove: [];
}>();

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
      classes: "bg-norlys-petroleum/10 text-norlys-petroleum-3",
      dot: "bg-norlys-petroleum animate-pulse",
    };
  return {
    text: "Afsluttet",
    classes: "bg-norlys-light-petroleum text-norlys-petroleum",
    dot: "bg-norlys-petroleum/40",
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

// ---------------------------------------------------------------------------
// Two simple 0–6 metrics (3 = neutral midpoint). We render "Neutral" when
// the conversation hasn't produced enough signal — a greeting alone shouldn't
// move the meter in either direction.
//
//   • Problem solved → ResolutionProgress
//   • Customer satisfied → CompanySatisfaction (falls back to CustomerMood)
//
// "Enough context" = at least 2 substantive customer turns AND analysis ready.
// ---------------------------------------------------------------------------
const SCALE_MAX = 6;
const NEUTRAL = 3;

const hasEnoughContext = computed(() => {
  const userTurns = props.session.transcript.filter(
    (t) => t.speaker === "user" && t.text.trim().length > 3,
  ).length;
  return userTurns >= 2 && props.session.analysis !== null;
});

interface Metric {
  label: string;
  value: number | null; // null = neutral / not enough context
}

const metrics = computed<Metric[]>(() => {
  if (!hasEnoughContext.value) {
    return [
      { label: "Problem løst", value: null },
      { label: "Tilfreds kunde", value: null },
    ];
  }
  const a = props.session.analysis!;
  const satisfaction = a.CompanySatisfaction ?? a.CustomerMood ?? NEUTRAL;
  return [
    { label: "Problem løst", value: a.ResolutionProgress ?? NEUTRAL },
    { label: "Tilfreds kunde", value: satisfaction },
  ];
});

// Render the 6 segments. Each segment's color reflects the bucket the score
// falls into. Filled = up to and including the score; empty = beyond it.
function dotClass(value: number | null, index: number) {
  // Neutral / no context: all segments muted, no fill direction.
  if (value === null) return "bg-norlys-light-petroleum";

  if (index >= value) return "bg-norlys-light-petroleum";

  // Color the FILLED segments by the score band.
  if (value <= 1) return "bg-norlys-red"; // very negative
  if (value <= 2) return "bg-amber-500"; // negative
  if (value <= 3) return "bg-norlys-petroleum/40"; // neutral
  if (value <= 4) return "bg-norlys-petroleum/70"; // mildly positive
  return "bg-norlys-petroleum"; // strongly positive
}

function valueLabel(value: number | null) {
  if (value === null) return "Neutral";
  if (value === NEUTRAL) return `${value} / ${SCALE_MAX} • Neutral`;
  return `${value} / ${SCALE_MAX}`;
}

function valueLabelClass(value: number | null) {
  if (value === null) return "text-norlys-petroleum/60";
  if (value <= 1) return "text-norlys-red font-semibold";
  if (value <= 2) return "text-amber-700 font-semibold";
  if (value <= 3) return "text-norlys-petroleum/70 font-semibold";
  return "text-norlys-petroleum-3 font-semibold";
}
</script>

<template>
  <article
    class="border border-norlys-light-petroleum rounded-xl overflow-hidden bg-white shadow-sm"
  >
    <!-- Header (always visible) -->
    <button
      type="button"
      class="w-full flex items-center gap-3 px-4 py-3 hover:bg-norlys-sand-2 text-left transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40 focus-visible:ring-inset"
      :aria-expanded="session.expanded"
      :aria-label="`Toggle call session details for ${session.customerName}`"
      @click="emit('toggle')"
    >
      <!-- Direction icon -->
      <div
        class="shrink-0 w-9 h-9 rounded-full flex items-center justify-center"
        :class="
          session.direction === 'outbound'
            ? 'bg-norlys-red/10 text-norlys-red'
            : 'bg-norlys-petroleum/10 text-norlys-petroleum'
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
          aria-hidden="true"
        >
          <path
            d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
          />
        </svg>
      </div>

      <div class="flex-1 min-w-0">
        <div class="flex items-center gap-2">
          <span
            class="font-headline text-sm font-bold text-norlys-petroleum-3 truncate"
            >{{ session.customerName }}</span
          >
          <span
            class="text-xs text-norlys-petroleum/60 tabular-nums shrink-0"
            >{{ session.phoneNumber }}</span
          >
        </div>
        <div
          class="flex items-center gap-2 mt-0.5 text-xs text-norlys-petroleum/70"
        >
          <span class="truncate">{{ session.personaLabel }}</span>
          <span class="text-norlys-light-petroleum-3">•</span>
          <span class="tabular-nums">{{ timeFmt(session.startedAt) }}</span>
        </div>
      </div>

      <!-- Status badge -->
      <span
        class="shrink-0 inline-flex items-center gap-1.5 text-xs font-semibold px-2 py-0.5 rounded-full"
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
        class="shrink-0 w-4 h-4 text-norlys-petroleum/60 transition-transform"
        :class="session.expanded ? 'rotate-180' : ''"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <polyline points="6 9 12 15 18 9" />
      </svg>
    </button>

    <!-- Expanded body — simple two-metric live snapshot -->
    <div v-if="session.expanded" class="border-t border-norlys-light-petroleum">
      <div class="px-4 py-4 space-y-3">
        <div
          v-for="m in metrics"
          :key="m.label"
          class="flex items-center gap-3"
        >
          <span
            class="font-headline text-sm font-bold text-norlys-petroleum-3 w-32 shrink-0"
            >{{ m.label }}</span
          >
          <div class="flex-1 flex items-center gap-1.5">
            <span
              v-for="i in SCALE_MAX"
              :key="i"
              class="h-2 flex-1 rounded-full transition-colors duration-500"
              :class="dotClass(m.value, i - 1)"
            />
          </div>
          <span
            class="text-sm tabular-nums w-24 text-right"
            :class="valueLabelClass(m.value)"
            >{{ valueLabel(m.value) }}</span
          >
        </div>

        <p
          v-if="!hasEnoughContext && session.status !== 'ended'"
          class="text-xs text-norlys-petroleum/60 italic text-left pt-1"
        >
          Venter på samtale…
        </p>
      </div>

      <!-- Footer actions -->
      <div
        class="flex items-center justify-end gap-2 px-3 py-2 border-t border-norlys-light-petroleum bg-norlys-sand-2/50"
      >
        <button
          v-if="session.status === 'ended'"
          type="button"
          class="text-xs text-norlys-petroleum hover:text-norlys-red px-2 py-1 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40 rounded"
          @click="emit('remove')"
        >
          Fjern fra liste
        </button>
      </div>
    </div>
  </article>
</template>
