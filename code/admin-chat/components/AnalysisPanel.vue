<script setup lang="ts">
import type {
    AnalysisCategory,
    AnalysisScores,
} from "~/composables/useCallAnalysis";
import { ANALYSIS_CATEGORIES } from "~/composables/useCallAnalysis";

const props = defineProps<{
  scores: AnalysisScores | null;
  isConnected: boolean;
  callEnded: boolean;
  callId: string | null;
  updateCount: number;
}>();

const emit = defineEmits<{
  close: [];
}>();

function barWidth(score: number): string {
  return `${(score / 5) * 100}%`;
}

function barColor(cat: AnalysisCategory, score: number): string {
  if (score === 0) return "bg-norlys-light-petroleum";
  if (cat.inverted) {
    // Higher = worse for inverted categories
    if (score >= 4) return "bg-norlys-red";
    if (score >= 3) return "bg-norlys-red/70";
    if (score >= 2) return "bg-amber-400";
    return "bg-norlys-petroleum";
  }
  // Higher = better for normal categories
  if (score >= 4) return "bg-norlys-petroleum";
  if (score >= 3) return "bg-norlys-petroleum/70";
  if (score >= 2) return "bg-amber-400";
  if (score >= 1) return "bg-norlys-red/60";
  return "bg-norlys-light-petroleum";
}

function scoreLabel(score: number): string {
  const labels = ["N/A", "Very Low", "Low", "Medium", "High", "Very High"];
  return labels[score] ?? "N/A";
}
</script>

<template>
  <aside
    class="w-full h-full border-r border-norlys-light-petroleum overflow-hidden flex flex-col shrink-0 bg-white"
  >
    <!-- Header -->
    <div
      class="px-4 py-3 border-b border-norlys-light-petroleum flex items-center justify-between"
    >
      <div class="flex items-center gap-2">
        <!-- Analytics icon -->
        <svg
          xmlns="http://www.w3.org/2000/svg"
          class="w-4 h-4 text-norlys-petroleum"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M3 3v18h18" />
          <path d="m19 9-5 5-4-4-3 3" />
        </svg>
        <span class="font-headline text-sm font-bold text-norlys-petroleum-3"
          >Live Analysis</span
        >
      </div>
      <div class="flex items-center gap-2">
        <!-- Status indicator -->
        <span
          class="flex items-center gap-1.5 text-[10px] font-medium px-2 py-0.5 rounded-full"
          :class="
            callEnded
              ? 'bg-norlys-light-petroleum text-norlys-petroleum'
              : isConnected
                ? 'bg-norlys-petroleum/10 text-norlys-petroleum-3'
                : 'bg-norlys-sand-2 text-norlys-petroleum/60'
          "
        >
          <span
            class="w-1.5 h-1.5 rounded-full"
            :class="
              callEnded
                ? 'bg-norlys-petroleum/40'
                : isConnected
                  ? 'bg-norlys-petroleum animate-pulse'
                  : 'bg-norlys-light-petroleum-3'
            "
          />
          {{
            callEnded
              ? "Ended"
              : isConnected
                ? `Live · #${updateCount}`
                : "Waiting"
          }}
        </span>
        <!-- Close button -->
        <button
          type="button"
          class="w-6 h-6 flex items-center justify-center rounded hover:bg-norlys-sand-2 text-norlys-petroleum/60 hover:text-norlys-petroleum-3 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40"
          aria-label="Close analysis panel"
          title="Close analysis"
          @click="emit('close')"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="w-3.5 h-3.5"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <line x1="18" y1="6" x2="6" y2="18" />
            <line x1="6" y1="6" x2="18" y2="18" />
          </svg>
        </button>
      </div>
    </div>

    <!-- Analysis content -->
    <div class="flex-1 overflow-y-auto px-3 py-3">
      <!-- Empty state: waiting for call -->
      <div
        v-if="!scores && !callEnded"
        class="flex flex-col items-center justify-center h-full text-center"
      >
        <div
          v-if="isConnected"
          class="flex flex-col items-center gap-3 text-norlys-petroleum/60"
        >
          <div class="relative">
            <svg
              xmlns="http://www.w3.org/2000/svg"
              class="w-10 h-10 text-norlys-light-petroleum-3"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M3 3v18h18" />
              <path d="m19 9-5 5-4-4-3 3" />
            </svg>
            <span
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-norlys-red/60 rounded-full animate-ping"
            />
            <span
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-norlys-red rounded-full"
            />
          </div>
          <div class="text-xs">
            <p class="font-medium text-norlys-petroleum">
              Analyzing conversation
            </p>
            <p class="text-norlys-petroleum/60 mt-0.5">
              Scores update after each speaker turn...
            </p>
          </div>
        </div>
        <div
          v-else
          class="flex flex-col items-center gap-2 text-norlys-petroleum/60 text-xs"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="w-8 h-8 text-norlys-light-petroleum"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M3 3v18h18" />
            <path d="m19 9-5 5-4-4-3 3" />
          </svg>
          <p>Waiting for call...</p>
        </div>
      </div>

      <!-- Call ended, no scores -->
      <div
        v-else-if="!scores && callEnded"
        class="flex flex-col items-center justify-center h-full text-center text-norlys-petroleum/60 text-xs"
      >
        <p>Call ended with no analysis data</p>
      </div>

      <!-- Score bars -->
      <div v-else-if="scores" class="space-y-2.5">
        <!-- Powered by badge -->
        <div
          class="flex items-center gap-1.5 text-[10px] text-norlys-petroleum/60 mb-3 px-1"
        >
          <span class="font-medium"
            >Powered by GPT-5.4-nano Structured Outputs</span
          >
        </div>

        <div v-for="cat in ANALYSIS_CATEGORIES" :key="cat.key" class="px-1">
          <!-- Label row -->
          <div class="flex items-center justify-between mb-1">
            <span class="text-[11px] text-norlys-ink flex items-center gap-1">
              <span aria-hidden="true">{{ cat.icon }}</span>
              <span class="font-medium">{{ cat.label }}</span>
            </span>
            <span
              class="text-[10px] font-semibold tabular-nums"
              :class="
                (scores[cat.key] as number) === 0
                  ? 'text-norlys-petroleum/50'
                  : cat.inverted && (scores[cat.key] as number) >= 3
                    ? 'text-norlys-red'
                    : !cat.inverted && (scores[cat.key] as number) >= 3
                      ? 'text-norlys-petroleum-3'
                      : 'text-norlys-ink'
              "
            >
              {{ scores[cat.key] }}/5
              <span class="text-norlys-petroleum/50 font-normal ml-0.5">{{
                scoreLabel(scores[cat.key] as number)
              }}</span>
            </span>
          </div>

          <!-- Bar -->
          <div class="h-2 bg-norlys-sand-2 rounded-full overflow-hidden">
            <div
              class="h-full rounded-full transition-all duration-700 ease-out"
              :class="barColor(cat, scores[cat.key] as number)"
              :style="{ width: barWidth(scores[cat.key] as number) }"
            />
          </div>
        </div>

        <!-- Last updated timestamp -->
        <div
          v-if="scores.Timestamp"
          class="text-[10px] text-norlys-petroleum/60 text-center mt-4 pt-3 border-t border-norlys-light-petroleum"
        >
          Last updated:
          {{
            new Date(scores.Timestamp).toLocaleTimeString([], {
              hour: "2-digit",
              minute: "2-digit",
              second: "2-digit",
            })
          }}
        </div>

        <!-- Call ended indicator -->
        <div
          v-if="callEnded"
          class="flex items-center gap-2 pt-2 text-[11px] text-norlys-petroleum/60"
        >
          <span class="flex-1 h-px bg-norlys-light-petroleum" />
          <span>Call ended</span>
          <span class="flex-1 h-px bg-norlys-light-petroleum" />
        </div>
      </div>
    </div>
  </aside>
</template>
