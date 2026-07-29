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
  if (speaker === "ai") return "text-norlys-red";
  if (speaker === "user") return "text-norlys-petroleum-3";
  return "text-norlys-petroleum/60";
}

function speakerBg(speaker: string): string {
  if (speaker === "ai") return "bg-norlys-red/5 border-norlys-red/20";
  if (speaker === "user")
    return "bg-norlys-light-petroleum/40 border-norlys-light-petroleum";
  return "bg-norlys-sand-2 border-norlys-light-petroleum";
}
</script>

<template>
  <aside
    class="w-full h-full overflow-hidden flex flex-col shrink-0 bg-white rounded-xl"
  >
    <!-- Header -->
    <div class="px-4 py-3 flex items-center justify-between">
      <div class="flex items-center gap-2">
        <!-- Phone icon -->
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
          <path
            d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
          />
        </svg>
        <span class="font-headline text-sm font-bold text-norlys-petroleum-3"
          >Live Transcription</span
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
          {{ callEnded ? "Ended" : isConnected ? "Live" : "Waiting" }}
        </span>
        <!-- Close button -->
        <button
          type="button"
          class="w-6 h-6 flex items-center justify-center rounded hover:bg-norlys-sand-2 text-norlys-petroleum/60 hover:text-norlys-petroleum-3 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40"
          aria-label="Close transcription panel"
          title="Close transcription"
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

    <!-- Transcript entries -->
    <div ref="scrollContainer" class="flex-1 overflow-y-auto px-3 py-3">
      <!-- Empty state -->
      <div
        v-if="entries.length === 0 && !callEnded"
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
              <path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z" />
              <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
              <line x1="12" x2="12" y1="19" y2="22" />
            </svg>
            <span
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-norlys-red/60 rounded-full animate-ping"
            />
            <span
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-norlys-red rounded-full"
            />
          </div>
          <div class="text-xs">
            <p class="font-medium text-norlys-petroleum">Call connected</p>
            <p class="text-norlys-petroleum/60 mt-0.5">
              Waiting for conversation to begin...
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
        class="flex flex-col items-center justify-center h-full text-center text-norlys-petroleum/60 text-xs"
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
            <span class="text-[10px] text-norlys-petroleum/60">{{
              formatTime(entry.timestamp)
            }}</span>
          </div>
          <p class="text-sm text-norlys-ink leading-relaxed">
            {{ entry.text }}
          </p>
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
