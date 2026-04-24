<script setup lang="ts">
import DOMPurify from "dompurify";
import { marked } from "marked";
import type { ChatMessage } from "~/composables/useAgentChat";

const props = defineProps<{ msg: ChatMessage; index: number }>();

// Configure marked for speed — synchronous, no sanitization overhead
marked.setOptions({ async: false, gfm: true, breaks: true });

// Parse markdown and sanitize to prevent XSS
const html = computed(() => {
  if (props.msg.role === "user" || !props.msg.content) return "";
  return DOMPurify.sanitize(marked.parse(props.msg.content) as string);
});
</script>

<template>
  <div
    class="flex gap-3"
    :class="msg.role === 'user' ? 'justify-end' : 'justify-start'"
  >
    <!-- Assistant messages (no bubble) -->
    <div v-if="msg.role !== 'user'" class="max-w-[80%]">
      <!-- Markdown content -->
      <div v-if="msg.content" class="prose prose-sm max-w-none" v-html="html" />

      <!-- Streaming indicator -->
      <span
        v-if="!msg.complete && msg.role === 'assistant'"
        class="inline-block w-1.5 h-4 bg-zinc-400 animate-pulse rounded-sm ml-0.5"
      />
    </div>

    <!-- User messages (light gray bubble) -->
    <div
      v-else
      class="max-w-[80%] rounded-2xl px-4 py-3 bg-zinc-100 text-zinc-900"
    >
      {{ msg.content }}
    </div>
  </div>
</template>
