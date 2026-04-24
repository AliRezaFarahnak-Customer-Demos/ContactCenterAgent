<script setup lang="ts">
const {
  messages,
  isLoading,
  threadId,
  error,
  sendMessage,
  stopGeneration,
  clearMessages,
} = useAgentChat();

const {
  entries: transcriptEntries,
  isConnected: transcriptConnected,
  callEnded: transcriptCallEnded,
  callId: transcriptCallId,
  connect: connectTranscription,
  clear: clearTranscription,
} = useCallTranscription();

const {
  scores: analysisScores,
  isConnected: analysisConnected,
  callEnded: analysisCallEnded,
  callId: analysisCallId,
  updateCount: analysisUpdateCount,
  connect: connectAnalysis,
  disconnect: disconnectAnalysis,
  clear: clearAnalysis,
} = useCallAnalysis();

const {
  entries: callLogEntries,
  isConnected: callLogConnected,
  unreadCount: callLogUnreadCount,
  connect: connectCallLog,
  clearUnread: clearCallLogUnread,
  clear: clearCallLog,
} = useCallLog();

// Only show live/active calls (filter out calls that have an "ended" entry)
const activeCallLogEntries = computed(() => {
  const endedContextIds = new Set(
    callLogEntries.value
      .filter((e) => e.direction === "ended")
      .map((e) => e.contextId),
  );
  return callLogEntries.value.filter((e) => !endedContextIds.has(e.contextId));
});

const config = useRuntimeConfig();
const input = ref("");
const chatContainer = ref<HTMLElement | null>(null);
const inputEl = ref<HTMLInputElement | null>(null);
// Activity panel hidden by default (code kept, just opted out)
const showActivity = ref(false);
const showTranscription = ref(false);
const showAnalysis = ref(false);
const showCallLog = ref(false);

// Auto-connect call log stream on page load
onMounted(() => {
  connectCallLog();
});

// Auto-hide call log when user starts chatting
watch(
  () => messages.value.length,
  (len) => {
    if (len > 0 && showCallLog.value) {
      showCallLog.value = false;
    }
  },
);

const suggestions = [
  {
    label: "📞 Make an AI call",
    message: "Make an AI call",
  },
];

async function send() {
  if (!input.value.trim()) return;
  sendMessage(input.value);
  input.value = "";
  await nextTick();
  inputEl.value?.focus();
}

function useSuggestion(msg: string) {
  // If chat is already active, clear everything first (fresh call)
  if (messages.value.length > 0) {
    clearMessages();
    clearTranscription();
    clearAnalysis();
    showTranscription.value = false;
    showAnalysis.value = false;
  }
  sendMessage(msg);
}

// Auto-scroll on new messages
watch(
  () => messages.value.length,
  async () => {
    await nextTick();
    chatContainer.value?.scrollTo({
      top: chatContainer.value.scrollHeight,
      behavior: "smooth",
    });
  },
);

// Also scroll during streaming (content changes)
watch(
  () => messages.value[messages.value.length - 1]?.content,
  async () => {
    await nextTick();
    chatContainer.value?.scrollTo({
      top: chatContainer.value.scrollHeight,
      behavior: "smooth",
    });
  },
);

// Refocus input when response finishes
watch(isLoading, async (loading) => {
  if (!loading) {
    await nextTick();
    inputEl.value?.focus();
  }
});

// Watch tool results for MakePhoneCall callId and auto-connect transcription
watch(
  () => messages.value.length,
  () => {
    // Look for the most recent tool result containing a callId
    for (let i = messages.value.length - 1; i >= 0; i--) {
      const msg = messages.value[i];
      if (
        msg.role === "tool" &&
        msg.toolName === "MakePhoneCall" &&
        msg.content
      ) {
        try {
          const result = JSON.parse(msg.content);
          // AG-UI serializes C# records with camelCase (web defaults)
          const id = result.callId ?? result.CallId;
          if (id && id !== transcriptCallId.value) {
            showTranscription.value = true;
            showAnalysis.value = true;
            connectTranscription(id);
            connectAnalysis(id);
          }
        } catch {
          // Try to extract callId from plain text result
          const match = msg.content.match(
            /callId["::]\s*["']?([a-f0-9-]{36})/i,
          );
          if (match && match[1] !== transcriptCallId.value) {
            showTranscription.value = true;
            showAnalysis.value = true;
            connectTranscription(match[1]);
            connectAnalysis(match[1]);
          }
        }
        break;
      }
    }
  },
);

// Watch for inbound calls in the call log and auto-connect transcription + analysis
watch(
  () => callLogEntries.value.length,
  () => {
    // Build a set of contextIds that have already ended so we skip them
    const endedContextIds = new Set(
      callLogEntries.value
        .filter((e) => e.direction === "ended")
        .map((e) => e.contextId),
    );

    // Find the most recent inbound call that we haven't connected to yet
    for (let i = callLogEntries.value.length - 1; i >= 0; i--) {
      const entry = callLogEntries.value[i];
      if (
        entry.direction === "inbound" &&
        entry.status === "connected" &&
        entry.contextId !== transcriptCallId.value &&
        !endedContextIds.has(entry.contextId)
      ) {
        showTranscription.value = true;
        showAnalysis.value = true;
        connectTranscription(entry.contextId);
        connectAnalysis(entry.contextId);
        break;
      }
    }
  },
);
</script>

<template>
  <div class="flex h-dvh w-screen bg-white text-zinc-900 relative">
    <!-- Analysis panel: sidebar on desktop, slide-over on mobile -->
    <div
      v-if="analysisCallId || analysisScores"
      class="fixed top-0 left-0 z-40 h-full w-80 transition-all duration-200 shadow-lg lg:static lg:z-auto lg:shrink-0 lg:shadow-none"
      :class="
        showAnalysis
          ? 'translate-x-0 lg:w-80'
          : '-translate-x-full lg:translate-x-0 lg:w-0 lg:overflow-hidden'
      "
    >
      <AnalysisPanel
        :scores="analysisScores"
        :is-connected="analysisConnected"
        :call-ended="analysisCallEnded"
        :call-id="analysisCallId"
        :update-count="analysisUpdateCount"
        @close="showAnalysis = false"
      />
    </div>

    <!-- Chat area -->
    <div class="flex-1 flex flex-col h-full min-w-0">
      <!-- Messages -->
      <div ref="chatContainer" class="flex-1 overflow-y-auto">
        <div
          class="px-4 py-6 space-y-4 max-w-3xl mx-auto"
          :class="
            messages.length === 0
              ? 'min-h-full flex flex-col justify-center py-8'
              : ''
          "
        >
          <!-- Welcome -->
          <div
            v-if="messages.length === 0"
            class="flex flex-col items-center text-center gap-6"
          >
            <!-- Use cases section -->
            <UseCasesSection />

            <!-- Action buttons row -->
            <div class="flex items-center gap-3 justify-center flex-wrap">
              <button
                v-for="s in suggestions"
                :key="s.label"
                class="inline-flex items-center gap-2 px-7 py-3.5 rounded-full bg-zinc-900 text-white text-base font-semibold hover:bg-zinc-700 hover:shadow-lg transition-all duration-200 active:scale-95"
                @click="useSuggestion(s.message)"
              >
                {{ s.label }}
              </button>
              <a
                href="https://www.linkedin.com/in/alirezafarahnak/"
                target="_blank"
                rel="noopener noreferrer"
                class="inline-flex items-center gap-2 px-7 py-3.5 rounded-full bg-[#0A66C2] text-white text-base font-semibold hover:bg-[#004182] hover:shadow-lg transition-all duration-200 active:scale-95"
              >
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  class="w-5 h-5"
                  viewBox="0 0 24 24"
                  fill="currentColor"
                >
                  <path
                    d="M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85 3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.062 2.062 0 0 1-2.063-2.065 2.064 2.064 0 1 1 2.063 2.065zm1.782 13.019H3.555V9h3.564v11.452zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0h.003z"
                  />
                </svg>
                <span class="hidden sm:inline">Contact me on LinkedIn</span>
              </a>
            </div>
          </div>

          <!-- Message list -->
          <ChatMessage
            v-for="(msg, i) in messages.filter(
              (m) =>
                m.role !== 'tool' &&
                !(m.role === 'assistant' && !m.content && m.toolCalls?.length),
            )"
            :key="msg.id"
            :msg="msg"
            :index="i"
          />

          <!-- Error -->
          <div v-if="error" class="text-center text-red-400 text-sm py-2">
            {{ error }}
          </div>
        </div>
      </div>

      <!-- Suggestion chips above input (always visible) -->
      <div
        v-if="messages.length > 0"
        class="flex flex-wrap gap-2 justify-center px-4 pb-2 max-w-3xl mx-auto w-full"
      >
        <button
          v-for="s in suggestions"
          :key="s.label"
          class="inline-flex items-center gap-2 px-7 py-3.5 rounded-full bg-zinc-900 text-white text-base font-semibold hover:bg-zinc-700 hover:shadow-lg transition-all duration-200 active:scale-95"
          @click="useSuggestion(s.message)"
        >
          {{ s.label }}
        </button>
        <button
          v-if="messages.length > 0"
          class="inline-flex items-center gap-2 px-7 py-3.5 rounded-full bg-red-600 text-white text-base font-semibold hover:bg-red-700 hover:shadow-lg transition-all duration-200 active:scale-95"
          @click="
            clearMessages();
            clearTranscription();
            clearAnalysis();
            showTranscription = false;
            showAnalysis = false;
          "
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="w-5 h-5"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <path
              d="m7 21-4.3-4.3c-1-1-1-2.5 0-3.4l9.6-9.6c1-1 2.5-1 3.4 0l5.6 5.6c1 1 1 2.5 0 3.4L13 21"
            />
            <path d="M22 21H7" />
            <path d="m5 11 9 9" />
          </svg>
          Clear
        </button>
      </div>

      <!-- Input bar -->
      <div
        class="px-4 py-3 max-w-3xl mx-auto w-full pb-[max(0.75rem,env(safe-area-inset-bottom))]"
      >
        <div
          class="flex items-center gap-3 border border-zinc-200 rounded-full px-5 py-3 bg-white shadow-md focus-within:shadow-lg focus-within:border-zinc-300 transition-all"
        >
          <input
            ref="inputEl"
            v-model="input"
            type="text"
            placeholder="Type a phone number to call..."
            class="flex-1 bg-transparent text-base text-zinc-900 placeholder:text-zinc-400 focus:outline-none"
            @keydown.enter.prevent="send"
          />
          <button
            v-if="isLoading"
            class="shrink-0 w-8 h-8 flex items-center justify-center rounded-full bg-zinc-900 hover:bg-zinc-700 text-white transition-colors"
            @click="stopGeneration"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              class="w-4 h-4"
              viewBox="0 0 24 24"
              fill="currentColor"
            >
              <rect x="6" y="6" width="12" height="12" rx="2" />
            </svg>
          </button>
          <button
            v-else
            class="shrink-0 w-8 h-8 flex items-center justify-center rounded-full transition-colors"
            :class="
              input.trim()
                ? 'bg-zinc-900 text-white hover:bg-zinc-700'
                : 'bg-zinc-200 text-zinc-400 cursor-default'
            "
            :disabled="!input.trim()"
            @click="send"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              class="w-4 h-4"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2.5"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <line x1="12" y1="19" x2="12" y2="5" />
              <polyline points="5 12 12 5 19 12" />
            </svg>
          </button>
        </div>
      </div>
    </div>

    <!-- Clear call button (appears after call ends, for both inbound and outbound) -->
    <button
      v-if="
        (transcriptCallEnded || analysisCallEnded) &&
        (transcriptCallId || analysisCallId)
      "
      class="fixed top-3 left-1/2 -translate-x-1/2 z-50 inline-flex items-center gap-2 px-5 py-2 rounded-full bg-zinc-800 text-white text-sm font-medium hover:bg-zinc-700 shadow-lg transition-all duration-200 active:scale-95"
      @click="
        clearTranscription();
        clearAnalysis();
        showTranscription = false;
        showAnalysis = false;
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
        <line x1="18" y1="6" x2="6" y2="18" />
        <line x1="6" y1="6" x2="18" y2="18" />
      </svg>
      Clear call
    </button>

    <!-- Analysis toggle button (left side) -->
    <button
      v-if="analysisCallId || analysisScores"
      class="fixed bottom-3 left-3 z-50 w-9 h-9 flex items-center justify-center rounded-full border border-zinc-200 bg-white/90 backdrop-blur text-zinc-500 hover:bg-zinc-100 hover:text-zinc-700 transition-colors shadow-sm"
      :class="
        showAnalysis ? 'bg-violet-50 text-violet-600 border-violet-200' : ''
      "
      @click="showAnalysis = !showAnalysis"
      title="Toggle conversation analysis"
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
        <path d="M3 3v18h18" />
        <path d="m19 9-5 5-4-4-3 3" />
      </svg>
      <!-- Live indicator dot -->
      <span
        v-if="analysisConnected && !analysisCallEnded"
        class="absolute -top-0.5 -right-0.5 w-2.5 h-2.5 bg-violet-500 rounded-full animate-pulse"
      />
    </button>

    <!-- Activity toggle button (hidden — activity panel opted out) -->
    <button
      v-if="false"
      class="fixed bottom-3 right-3 z-50 w-9 h-9 flex items-center justify-center rounded-full border border-zinc-200 bg-white/90 backdrop-blur text-zinc-500 hover:bg-zinc-100 hover:text-zinc-700 transition-colors shadow-sm"
      :class="showActivity ? 'bg-zinc-100 text-zinc-700' : ''"
      @click="showActivity = !showActivity"
      title="Toggle activity log"
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
        <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
        <circle cx="9" cy="7" r="4" />
        <path d="M22 21v-2a4 4 0 0 0-3-3.87" />
        <path d="M16 3.13a4 4 0 0 1 0 7.75" />
      </svg>
    </button>

    <!-- Transcription toggle button -->
    <button
      v-if="transcriptCallId"
      class="fixed bottom-3 right-14 z-50 w-9 h-9 flex items-center justify-center rounded-full border border-zinc-200 bg-white/90 backdrop-blur text-zinc-500 hover:bg-zinc-100 hover:text-zinc-700 transition-colors shadow-sm"
      :class="
        showTranscription
          ? 'bg-emerald-50 text-emerald-600 border-emerald-200'
          : ''
      "
      @click="showTranscription = !showTranscription"
      title="Toggle live transcription"
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
      <!-- Live indicator dot -->
      <span
        v-if="transcriptConnected && !transcriptCallEnded"
        class="absolute -top-0.5 -right-0.5 w-2.5 h-2.5 bg-emerald-500 rounded-full animate-pulse"
      />
    </button>

    <!-- Call log panel: sidebar on desktop, slide-over on mobile (only on welcome screen) -->
    <div
      v-if="showCallLog && messages.length === 0"
      class="fixed top-0 left-0 z-40 h-full w-80 transition-all duration-200 shadow-lg lg:static lg:z-auto lg:shrink-0 lg:shadow-none translate-x-0 lg:w-80"
    >
      <CallLogPanel
        :entries="activeCallLogEntries"
        :is-connected="callLogConnected"
        @close="showCallLog = false"
      />
    </div>

    <!-- Backdrop overlay on mobile (call log) -->
    <div
      v-if="showCallLog && messages.length === 0"
      class="fixed inset-0 z-30 bg-black/20 backdrop-blur-sm lg:hidden"
      @click="showCallLog = false"
    />

    <!-- Backdrop overlay on mobile (analysis) -->
    <div
      v-if="showAnalysis && (analysisCallId || analysisScores)"
      class="fixed inset-0 z-40 bg-black/20 backdrop-blur-sm lg:hidden"
      @click="showAnalysis = false"
    />

    <!-- Backdrop overlay on mobile (activity — hidden) -->
    <div
      v-if="false && showActivity"
      class="fixed inset-0 z-40 bg-black/20 backdrop-blur-sm lg:hidden"
      @click="showActivity = false"
    />

    <!-- Backdrop overlay on mobile (transcription) -->
    <div
      v-if="showTranscription && transcriptCallId"
      class="fixed inset-0 z-40 bg-black/20 backdrop-blur-sm lg:hidden"
      @click="showTranscription = false"
    />

    <!-- Transcription panel: sidebar on desktop, slide-over on mobile -->
    <div
      v-if="transcriptCallId"
      class="fixed top-0 right-0 z-40 h-full w-80 transition-all duration-200 shadow-lg lg:static lg:z-auto lg:shrink-0 lg:shadow-none"
      :class="
        showTranscription
          ? 'translate-x-0 lg:w-80'
          : 'translate-x-full lg:translate-x-0 lg:w-0 lg:overflow-hidden'
      "
    >
      <TranscriptionPanel
        :entries="transcriptEntries"
        :is-connected="transcriptConnected"
        :call-ended="transcriptCallEnded"
        :call-id="transcriptCallId"
        @close="showTranscription = false"
      />
    </div>

    <!-- Activity panel: sidebar on desktop, slide-over on mobile (hidden — opted out) -->
    <div
      v-if="false"
      class="fixed top-0 right-0 z-40 h-full w-72 transition-all duration-200 shadow-lg lg:static lg:z-auto lg:shrink-0 lg:shadow-none"
      :class="
        showActivity
          ? 'translate-x-0 lg:w-64'
          : 'translate-x-full lg:translate-x-0 lg:w-0 lg:overflow-hidden'
      "
    >
      <ActivityPanel
        :messages="messages"
        :is-loading="isLoading"
        :thread-id="threadId"
        :error="error"
      />
    </div>

    <!-- Incoming calls toggle button (bottom-right, subtle) -->
    <button
      v-if="messages.length === 0"
      class="fixed bottom-3 right-3 z-50 hidden items-center gap-1.5 px-3 py-1.5 rounded-full text-xs font-medium transition-all duration-200 lg:inline-flex"
      :class="
        showCallLog
          ? 'bg-zinc-700 text-white hover:bg-zinc-600'
          : 'bg-zinc-100 text-zinc-500 hover:bg-zinc-200 hover:text-zinc-700'
      "
      @click="
        showCallLog = !showCallLog;
        if (showCallLog) clearCallLogUnread();
      "
      title="Toggle incoming call log"
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
        <polyline points="7 10 12 15 17 10" />
        <line x1="12" y1="15" x2="12" y2="3" />
        <path
          d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
        />
      </svg>
      <span
        v-if="activeCallLogEntries.length > 0 && !showCallLog"
        class="inline-flex items-center justify-center min-w-[16px] h-4 rounded-full bg-red-500 text-white text-[10px] font-bold px-1"
        >{{ activeCallLogEntries.length }}</span
      >
      Incoming
    </button>

    <!-- Build version badge -->
    <div
      class="hidden lg:block fixed bottom-1 left-2 z-30 text-[10px] text-zinc-300 select-none pointer-events-none"
    >
      v{{ config.public.appVersion }}
    </div>
  </div>
</template>
