<script setup lang="ts">
import { nextTick, ref, watch } from "vue";
import type { ChatMessage } from "~/composables/useAgentChat";

const props = defineProps<{
  messages: readonly ChatMessage[];
  isLoading: boolean;
  threadId: string;
  error: string | null;
}>();

const logContainer = ref<HTMLElement | null>(null);
const hoveredIndex = ref<number | null>(null);
const popoverStyle = ref<Record<string, string>>({});

// Auto-scroll to bottom when messages change
watch(
  () => props.messages.length,
  async () => {
    await nextTick();
    if (logContainer.value) {
      logContainer.value.scrollTop = logContainer.value.scrollHeight;
    }
  },
);

function roleLabel(role: string) {
  if (role === "user") return "User";
  if (role === "assistant") return "AI";
  return "Tool";
}

/** Look up the tool call arguments for a tool-result message */
function getToolArgs(msg: ChatMessage): string | undefined {
  if (msg.role !== "tool" || !msg.toolCallId) return undefined;
  for (const m of props.messages) {
    if (m.role === "assistant" && m.toolCalls?.length) {
      const tc = m.toolCalls.find((t) => t.id === msg.toolCallId);
      if (tc?.function.arguments) return tc.function.arguments;
    }
  }
  return undefined;
}

function showPopover(i: number, event: MouseEvent) {
  hoveredIndex.value = i;
  const el = event.currentTarget as HTMLElement;
  const rect = el.getBoundingClientRect();
  // Position popover to the left of the activity panel
  popoverStyle.value = {
    position: "fixed",
    top: `${Math.min(rect.top, window.innerHeight - 400)}px`,
    right: `${window.innerWidth - rect.left + 8}px`,
  };
}
</script>

<template>
  <aside
    class="w-full h-full border-l border-zinc-200 overflow-hidden text-[11px] font-mono text-zinc-500 flex flex-col shrink-0 bg-white"
  >
    <!-- Header -->
    <div
      class="px-4 py-3 border-b border-zinc-100 flex items-center justify-between"
    >
      <div class="flex items-center gap-2">
        <!-- Admin icon -->
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
          <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
          <circle cx="9" cy="7" r="4" />
          <path d="M22 21v-2a4 4 0 0 0-3-3.87" />
          <path d="M16 3.13a4 4 0 0 1 0 7.75" />
        </svg>
        <span class="text-sm font-semibold text-zinc-700">Activity</span>
      </div>
      <div class="flex items-center gap-2">
        <!-- Status indicator -->
        <span
          class="flex items-center gap-1.5 text-[10px] font-medium px-2 py-0.5 rounded-full"
          :class="
            isLoading
              ? 'bg-amber-50 text-amber-600'
              : 'bg-emerald-50 text-emerald-600'
          "
        >
          <span
            class="w-1.5 h-1.5 rounded-full"
            :class="isLoading ? 'bg-amber-400 animate-pulse' : 'bg-emerald-400'"
          />
          {{ isLoading ? "Generating" : "Idle" }}
        </span>
        <span class="text-[10px] text-zinc-400"
          >{{ messages.length }} msgs</span
        >
      </div>
    </div>

    <!-- Message log -->
    <div ref="logContainer" class="flex-1 overflow-y-auto px-3 pb-2 space-y-1">
      <template v-for="(msg, i) in messages" :key="msg.id">
        <!-- Hide assistant messages that only contain tool calls (no text).
           The Tool row already shows the tool name, args, and result. -->
        <div
          v-if="
            !(msg.role === 'assistant' && !msg.content && msg.toolCalls?.length)
          "
          class="py-1 border-b border-zinc-100 last:border-0 cursor-pointer hover:bg-zinc-50 rounded transition-colors"
          @mouseenter="showPopover(i, $event)"
          @mouseleave="hoveredIndex = null"
        >
          <div class="flex items-center gap-1">
            <span
              :class="{
                'text-blue-400': msg.role === 'user',
                'text-emerald-500': msg.role === 'assistant',
                'text-orange-400': msg.role === 'tool',
              }"
            >
              {{
                msg.role === "user"
                  ? "User"
                  : msg.role === "assistant"
                    ? "Agent"
                    : "Tool"
              }}
            </span>
            <span class="text-zinc-300">#{{ i + 1 }}</span>
            <span
              v-if="msg.role === 'tool' && msg.toolName"
              class="text-purple-400 truncate"
              >{{ msg.toolName }}</span
            >
          </div>
          <div v-if="msg.role === 'tool'" class="mt-0.5 space-y-0.5">
            <div
              v-if="getToolArgs(msg)"
              class="text-[10px] text-zinc-400 font-mono truncate"
            >
              <span class="text-purple-300">in:</span>
              {{ getToolArgs(msg)!.slice(0, 72) }}
            </div>
            <div
              v-if="msg.content"
              class="text-[10px] text-zinc-400 font-mono truncate"
            >
              <span class="text-emerald-400">out:</span>
              {{ msg.content.slice(0, 70) }}
            </div>
          </div>
          <div v-else-if="msg.content" class="text-zinc-400 truncate">
            {{ msg.content.slice(0, 80) }}
          </div>
        </div>
      </template>

      <!-- Error / policy block -->
      <div
        v-if="error"
        class="py-1.5 px-2 rounded bg-red-50 border border-red-200 text-red-600"
      >
        <div
          class="flex items-center gap-1 font-semibold text-[10px] uppercase tracking-wide mb-0.5"
        >
          <span class="w-1.5 h-1.5 rounded-full bg-red-400" />
          Error
        </div>
        <div class="truncate">{{ error }}</div>
      </div>

      <div
        v-if="messages.length === 0 && !error"
        class="text-zinc-300 italic py-2"
      >
        No activity
      </div>
    </div>

    <!-- Version -->
    <div
      class="px-3 py-2 border-t border-zinc-100 text-[10px] text-zinc-400 font-mono text-center select-none"
    >
      v{{ useRuntimeConfig().public.appVersion }}
    </div>
  </aside>

  <!-- Fixed popover (rendered outside aside to avoid overflow clipping) -->
  <Teleport to="body">
    <div
      v-if="hoveredIndex !== null && messages[hoveredIndex]"
      :style="popoverStyle"
      class="w-[40rem] max-h-[48rem] overflow-y-auto bg-white border border-zinc-200 rounded-lg shadow-lg p-6 text-base font-sans text-zinc-700 z-[100]"
    >
      <!-- Header -->
      <div class="flex items-center gap-2 mb-2 pb-2 border-b border-zinc-100">
        <span
          class="px-3 py-1 rounded text-[20px] font-semibold uppercase tracking-wide"
          :class="{
            'bg-blue-100 text-blue-700': messages[hoveredIndex].role === 'user',
            'bg-emerald-100 text-emerald-700':
              messages[hoveredIndex].role === 'assistant',
            'bg-orange-100 text-orange-700':
              messages[hoveredIndex].role === 'tool',
          }"
        >
          {{ roleLabel(messages[hoveredIndex].role) }}
        </span>
        <span class="text-zinc-400 text-[20px]">#{{ hoveredIndex + 1 }}</span>
        <span
          v-if="
            messages[hoveredIndex].role === 'tool' &&
            messages[hoveredIndex].toolName
          "
          class="text-purple-600 font-mono text-[20px]"
        >
          {{ messages[hoveredIndex].toolName }}
        </span>
      </div>

      <!-- Tool message: show args (in) + result (out) together -->
      <div v-if="messages[hoveredIndex].role === 'tool'" class="space-y-2">
        <div v-if="getToolArgs(messages[hoveredIndex])">
          <div
            class="text-[20px] font-semibold uppercase tracking-wide text-purple-500 mb-1"
          >
            Input
          </div>
          <pre
            class="text-[20px] text-zinc-500 whitespace-pre-wrap break-words bg-zinc-50 rounded p-4"
            >{{ getToolArgs(messages[hoveredIndex]) }}</pre
          >
        </div>
        <div v-if="messages[hoveredIndex].content">
          <div
            class="text-[20px] font-semibold uppercase tracking-wide text-emerald-500 mb-1"
          >
            Output
          </div>
          <pre
            class="text-[20px] text-zinc-500 whitespace-pre-wrap break-words bg-zinc-50 rounded p-4"
            >{{ messages[hoveredIndex].content }}</pre
          >
        </div>
      </div>

      <!-- Non-tool messages: content + tool calls -->
      <template v-else>
        <div
          v-if="messages[hoveredIndex].content"
          class="whitespace-pre-wrap break-words text-zinc-600 leading-relaxed"
        >
          {{ messages[hoveredIndex].content }}
        </div>

        <div
          v-if="messages[hoveredIndex].toolCalls?.length"
          class="mt-2 space-y-2"
        >
          <div
            v-for="tc in messages[hoveredIndex].toolCalls"
            :key="tc.id"
            class="bg-zinc-50 rounded p-2"
          >
            <div class="font-semibold text-purple-600 mb-1">
              {{ tc.function.name }}
            </div>
            <pre
              v-if="tc.function.arguments"
              class="text-[20px] text-zinc-500 whitespace-pre-wrap break-words"
              >{{ tc.function.arguments }}</pre
            >
          </div>
        </div>
      </template>

      <div
        v-if="
          !messages[hoveredIndex].content &&
          !messages[hoveredIndex].toolCalls?.length
        "
        class="text-zinc-400 italic"
      >
        No content
      </div>
    </div>
  </Teleport>
</template>
