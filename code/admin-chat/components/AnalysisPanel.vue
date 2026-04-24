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
  if (score === 0) return "bg-zinc-200";
  if (cat.inverted) {
    // Higher = worse for inverted categories
    if (score >= 4) return "bg-red-500";
    if (score >= 3) return "bg-orange-400";
    if (score >= 2) return "bg-amber-400";
    return "bg-emerald-400";
  }
  // Higher = better for normal categories
  if (score >= 4) return "bg-emerald-500";
  if (score >= 3) return "bg-emerald-400";
  if (score >= 2) return "bg-amber-400";
  if (score >= 1) return "bg-orange-400";
  return "bg-zinc-200";
}

function scoreLabel(score: number): string {
  const labels = ["N/A", "Very Low", "Low", "Medium", "High", "Very High"];
  return labels[score] ?? "N/A";
}
</script>

<template>
  <aside
    class="w-full h-full border-r border-zinc-200 overflow-hidden flex flex-col shrink-0 bg-white"
  >
    <!-- Header -->
    <div
      class="px-4 py-3 border-b border-zinc-100 flex items-center justify-between"
    >
      <div class="flex items-center gap-2">
        <!-- Analytics icon -->
        <svg
          xmlns="http://www.w3.org/2000/svg"
          class="w-4 h-4 text-zinc-500"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
        >
          <path d="M3 3v18h18" />
          <path d="m19 9-5 5-4-4-3 3" />
        </svg>
        <span class="text-sm font-semibold text-zinc-700">Live Analysis</span>
      </div>
      <div class="flex items-center gap-2">
        <!-- Status indicator -->
        <span
          class="flex items-center gap-1.5 text-[10px] font-medium px-2 py-0.5 rounded-full"
          :class="
            callEnded
              ? 'bg-zinc-100 text-zinc-500'
              : isConnected
                ? 'bg-violet-50 text-violet-600'
                : 'bg-zinc-100 text-zinc-400'
          "
        >
          <span
            class="w-1.5 h-1.5 rounded-full"
            :class="
              callEnded
                ? 'bg-zinc-400'
                : isConnected
                  ? 'bg-violet-500 animate-pulse'
                  : 'bg-zinc-300'
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
          class="w-6 h-6 flex items-center justify-center rounded hover:bg-zinc-100 text-zinc-400 hover:text-zinc-600 transition-colors"
          @click="emit('close')"
          title="Close analysis"
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
          class="flex flex-col items-center gap-3 text-zinc-400"
        >
          <div class="relative">
            <svg
              xmlns="http://www.w3.org/2000/svg"
              class="w-10 h-10 text-zinc-300"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path d="M3 3v18h18" />
              <path d="m19 9-5 5-4-4-3 3" />
            </svg>
            <span
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-violet-400 rounded-full animate-ping"
            />
            <span
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-violet-500 rounded-full"
            />
          </div>
          <div class="text-xs">
            <p class="font-medium text-zinc-500">Analyzing conversation</p>
            <p class="text-zinc-400 mt-0.5">
              Scores update after each speaker turn...
            </p>
          </div>
        </div>
        <div
          v-else
          class="flex flex-col items-center gap-2 text-zinc-400 text-xs"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="w-8 h-8 text-zinc-200"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linecap="round"
            stroke-linejoin="round"
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
        class="flex flex-col items-center justify-center h-full text-center text-zinc-400 text-xs"
      >
        <p>Call ended with no analysis data</p>
      </div>

      <!-- Score bars -->
      <div v-else-if="scores" class="space-y-2.5">
        <!-- Powered by badge -->
        <div
          class="flex items-center gap-1.5 text-[10px] text-zinc-400 mb-3 px-1"
        >
          <span class="font-medium">Powered by GPT-5.2 Structured Outputs</span>
        </div>

        <div v-for="cat in ANALYSIS_CATEGORIES" :key="cat.key" class="px-1">
          <!-- Label row -->
          <div class="flex items-center justify-between mb-1">
            <span class="text-[11px] text-zinc-600 flex items-center gap-1">
              <span>{{ cat.icon }}</span>
              <span class="font-medium">{{ cat.label }}</span>
            </span>
            <span
              class="text-[10px] font-semibold tabular-nums"
              :class="
                (scores[cat.key] as number) === 0
                  ? 'text-zinc-400'
                  : cat.inverted && (scores[cat.key] as number) >= 3
                    ? 'text-red-500'
                    : !cat.inverted && (scores[cat.key] as number) >= 3
                      ? 'text-emerald-600'
                      : 'text-zinc-600'
              "
            >
              {{ scores[cat.key] }}/5
              <span class="text-zinc-400 font-normal ml-0.5">{{
                scoreLabel(scores[cat.key] as number)
              }}</span>
            </span>
          </div>

          <!-- Bar -->
          <div class="h-2 bg-zinc-100 rounded-full overflow-hidden">
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
          class="text-[10px] text-zinc-400 text-center mt-4 pt-3 border-t border-zinc-100"
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
          class="flex items-center gap-2 pt-2 text-[11px] text-zinc-400"
        >
          <span class="flex-1 h-px bg-zinc-200" />
          <span>Call ended</span>
          <span class="flex-1 h-px bg-zinc-200" />
        </div>
      </div>
    </div>
  </aside>
</template>
