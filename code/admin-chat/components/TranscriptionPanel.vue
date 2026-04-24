<script setup lang="ts">
import { nextTick, ref, watch } from "vue";
import type { TranscriptEntry } from "~/composables/useCallTranscription";

const props = defineProps<{
  entries: readonly TranscriptEntry[];
  isConnected: boolean;
  callEnded: boolean;
  callId: string | null;
}>();

const emit = defineEmits<{
  close: [];
}>();

const scrollContainer = ref<HTMLElement | null>(null);

// Auto-scroll when new entries arrive
watch(
  () => props.entries.length,
  async () => {
    await nextTick();
    if (scrollContainer.value) {
      scrollContainer.value.scrollTop = scrollContainer.value.scrollHeight;
    }
  },
);

function formatTime(timestamp: string): string {
  try {
    return new Date(timestamp).toLocaleTimeString([], {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
  } catch {
    return "";
  }
}

function speakerLabel(speaker: string): string {
  if (speaker === "ai") return "AI Agent";
  if (speaker === "user") return "Caller";
  return "System";
}

function speakerColor(speaker: string): string {
  if (speaker === "ai") return "text-emerald-600";
  if (speaker === "user") return "text-blue-600";
  return "text-zinc-400";
}

function speakerBg(speaker: string): string {
  if (speaker === "ai") return "bg-emerald-50 border-emerald-100";
  if (speaker === "user") return "bg-blue-50 border-blue-100";
  return "bg-zinc-50 border-zinc-100";
}
</script>

<template>
  <aside
    class="w-full h-full border-l border-zinc-200 overflow-hidden flex flex-col shrink-0 bg-white"
  >
    <!-- Header -->
    <div
      class="px-4 py-3 border-b border-zinc-100 flex items-center justify-between"
    >
      <div class="flex items-center gap-2">
        <!-- Phone icon -->
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
          <path
            d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
          />
        </svg>
        <span class="text-sm font-semibold text-zinc-700"
          >Live Transcription</span
        >
      </div>
      <div class="flex items-center gap-2">
        <!-- Status indicator -->
        <span
          class="flex items-center gap-1.5 text-[10px] font-medium px-2 py-0.5 rounded-full"
          :class="
            callEnded
              ? 'bg-zinc-100 text-zinc-500'
              : isConnected
                ? 'bg-emerald-50 text-emerald-600'
                : 'bg-zinc-100 text-zinc-400'
          "
        >
          <span
            class="w-1.5 h-1.5 rounded-full"
            :class="
              callEnded
                ? 'bg-zinc-400'
                : isConnected
                  ? 'bg-emerald-500 animate-pulse'
                  : 'bg-zinc-300'
            "
          />
          {{ callEnded ? "Ended" : isConnected ? "Live" : "Waiting" }}
        </span>
        <!-- Close button -->
        <button
          class="w-6 h-6 flex items-center justify-center rounded hover:bg-zinc-100 text-zinc-400 hover:text-zinc-600 transition-colors"
          @click="emit('close')"
          title="Close transcription"
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

    <!-- Transcript entries -->
    <div ref="scrollContainer" class="flex-1 overflow-y-auto px-3 py-3">
      <!-- Empty state -->
      <div
        v-if="entries.length === 0 && !callEnded"
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
              <path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z" />
              <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
              <line x1="12" x2="12" y1="19" y2="22" />
            </svg>
            <span
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-emerald-400 rounded-full animate-ping"
            />
            <span
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-emerald-500 rounded-full"
            />
          </div>
          <div class="text-xs">
            <p class="font-medium text-zinc-500">Call connected</p>
            <p class="text-zinc-400 mt-0.5">
              Waiting for conversation to begin...
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
            <path
              d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
            />
          </svg>
          <p>Dialing...</p>
        </div>
      </div>

      <!-- Call ended, no entries -->
      <div
        v-else-if="entries.length === 0 && callEnded"
        class="flex flex-col items-center justify-center h-full text-center text-zinc-400 text-xs"
      >
        <p>Call ended with no transcription</p>
      </div>

      <!-- Transcript messages -->
      <div v-else class="space-y-2">
        <div
          v-for="(entry, i) in entries"
          :key="i"
          class="rounded-lg border px-3 py-2"
          :class="speakerBg(entry.speaker)"
        >
          <div class="flex items-center justify-between mb-0.5">
            <span
              class="text-[11px] font-semibold"
              :class="speakerColor(entry.speaker)"
            >
              {{ speakerLabel(entry.speaker) }}
            </span>
            <span class="text-[10px] text-zinc-400">{{
              formatTime(entry.timestamp)
            }}</span>
          </div>
          <p class="text-sm text-zinc-800 leading-relaxed">{{ entry.text }}</p>
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
