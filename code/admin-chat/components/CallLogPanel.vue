<script setup lang="ts">
import { nextTick, ref, watch } from "vue";
import type { CallLogEntry } from "~/composables/useCallLog";

const props = defineProps<{
  entries: readonly CallLogEntry[];
  isConnected: boolean;
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

function formatDate(timestamp: string): string {
  try {
    return new Date(timestamp).toLocaleDateString([], {
      month: "short",
      day: "numeric",
    });
  } catch {
    return "";
  }
}

function formatPhoneNumber(phone: string): string {
  if (!phone) return "Unknown";
  return phone.trim();
}

function getDirectionLabel(entry: CallLogEntry): string {
  if (entry.direction === "inbound") return "Incoming";
  if (entry.direction === "outbound") return "Outgoing";
  if (entry.direction === "ended") return "Ended";
  return entry.direction;
}

function getStatusColor(entry: CallLogEntry): string {
  if (entry.direction === "ended" || entry.status === "disconnected")
    return "bg-norlys-sand-2 border-norlys-light-petroleum";
  if (entry.direction === "inbound")
    return "bg-norlys-petroleum/5 border-norlys-petroleum/20";
  if (entry.direction === "outbound")
    return "bg-norlys-red/5 border-norlys-red/20";
  return "bg-norlys-sand-2 border-norlys-light-petroleum";
}

function getDirectionColor(entry: CallLogEntry): string {
  if (entry.direction === "ended" || entry.status === "disconnected")
    return "text-norlys-petroleum/60";
  if (entry.direction === "inbound") return "text-norlys-petroleum-3";
  if (entry.direction === "outbound") return "text-norlys-red";
  return "text-norlys-petroleum/60";
}
</script>

<template>
  <aside
    class="w-full h-full border-l border-norlys-light-petroleum overflow-hidden flex flex-col shrink-0 bg-white"
  >
    <!-- Header -->
    <div
      class="px-4 py-3 border-b border-norlys-light-petroleum flex items-center justify-between"
    >
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
        <div class="flex flex-col">
          <div class="flex items-center gap-1.5">
            <span
              class="font-headline text-sm font-bold text-norlys-petroleum-3"
              >Call Log</span
            >
            <span
              v-if="entries.length > 0"
              class="text-[10px] font-medium text-norlys-petroleum bg-norlys-light-petroleum px-1.5 py-0.5 rounded-full"
            >
              {{ entries.length }}
            </span>
          </div>
          <span class="text-xs text-norlys-petroleum tabular-nums"
            >+1 (269) 692-5682</span
          >
        </div>
      </div>
      <div class="flex items-center gap-2">
        <!-- Status indicator -->
        <span
          class="flex items-center gap-1.5 text-[10px] font-medium px-2 py-0.5 rounded-full"
          :class="
            isConnected
              ? 'bg-norlys-petroleum/10 text-norlys-petroleum-3'
              : 'bg-norlys-sand-2 text-norlys-petroleum/60'
          "
        >
          <span
            class="w-1.5 h-1.5 rounded-full"
            :class="
              isConnected
                ? 'bg-norlys-petroleum animate-pulse'
                : 'bg-norlys-light-petroleum-3'
            "
          />
          {{ isConnected ? "Live" : "Disconnected" }}
        </span>
        <!-- Close button -->
        <button
          type="button"
          class="w-6 h-6 flex items-center justify-center rounded hover:bg-norlys-sand-2 text-norlys-petroleum/60 hover:text-norlys-petroleum-3 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40"
          aria-label="Close call log"
          title="Close call log"
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

    <!-- Call log entries -->
    <div ref="scrollContainer" class="flex-1 overflow-y-auto px-3 py-3">
      <!-- Empty state -->
      <div
        v-if="entries.length === 0"
        class="flex flex-col items-center justify-center h-full text-center"
      >
        <div class="flex flex-col items-center gap-3 text-norlys-petroleum/60">
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
              <path
                d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
              />
            </svg>
            <span
              v-if="isConnected"
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-norlys-red/60 rounded-full animate-ping"
            />
            <span
              v-if="isConnected"
              class="absolute -top-0.5 -right-0.5 w-3 h-3 bg-norlys-red rounded-full"
            />
          </div>
          <div class="text-xs">
            <p class="font-medium text-norlys-petroleum">
              {{ isConnected ? "Listening for calls" : "Call Log" }}
            </p>
            <p class="text-norlys-petroleum/60 mt-0.5">
              {{
                isConnected
                  ? "Waiting for incoming or outgoing calls..."
                  : "Connect to start tracking calls"
              }}
            </p>
          </div>
        </div>
      </div>

      <!-- Call log entry list -->
      <div v-else class="space-y-2">
        <div
          v-for="(entry, i) in entries"
          :key="i"
          class="rounded-lg border px-3 py-2"
          :class="getStatusColor(entry)"
        >
          <div class="flex items-center justify-between mb-1">
            <div class="flex items-center gap-1.5">
              <!-- Direction icon -->
              <svg
                v-if="entry.direction === 'inbound'"
                xmlns="http://www.w3.org/2000/svg"
                class="w-3 h-3 text-norlys-petroleum"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2.5"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <polyline points="7 10 12 15 17 10" />
                <line x1="12" y1="15" x2="12" y2="3" />
              </svg>
              <svg
                v-else-if="entry.direction === 'outbound'"
                xmlns="http://www.w3.org/2000/svg"
                class="w-3 h-3 text-norlys-red"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2.5"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <polyline points="17 14 12 9 7 14" />
                <line x1="12" y1="9" x2="12" y2="21" />
              </svg>
              <svg
                v-else
                xmlns="http://www.w3.org/2000/svg"
                class="w-3 h-3 text-norlys-petroleum/60"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2.5"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <line x1="18" y1="6" x2="6" y2="18" />
                <line x1="6" y1="6" x2="18" y2="18" />
              </svg>
              <span
                class="text-[11px] font-semibold"
                :class="getDirectionColor(entry)"
              >
                {{ getDirectionLabel(entry) }}
              </span>
            </div>
            <div
              class="flex items-center gap-1.5 text-[10px] text-norlys-petroleum/60"
            >
              <span>{{ formatDate(entry.timestamp) }}</span>
              <span>{{ formatTime(entry.timestamp) }}</span>
            </div>
          </div>
          <div class="flex items-center gap-1.5">
            <span class="text-sm font-medium text-norlys-ink">
              {{ entry.name || formatPhoneNumber(entry.phoneNumber) }}
            </span>
            <span
              v-if="entry.name"
              class="text-[11px] text-norlys-petroleum/60 tabular-nums"
            >
              {{ formatPhoneNumber(entry.phoneNumber) }}
            </span>
          </div>
          <p
            v-if="entry.purpose"
            class="text-xs text-norlys-ink/70 mt-0.5 leading-relaxed"
          >
            {{ entry.purpose }}
          </p>
        </div>
      </div>
    </div>
  </aside>
</template>
