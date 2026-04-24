<script setup lang="ts">
const {
  messages,
  isLoading,
  error,
  sendMessage,
  stopGeneration,
  clearMessages,
} = useAgentChat();

const { sessions, addSession, removeSession, clearEnded, toggleExpanded } =
  useCallSessions();

const config = useRuntimeConfig();
const input = ref("");
const chatContainer = ref<HTMLElement | null>(null);
const inputEl = ref<HTMLInputElement | null>(null);
const callError = ref<string | null>(null);

async function send() {
  if (!input.value.trim()) return;
  sendMessage(input.value);
  input.value = "";
  await nextTick();
  inputEl.value?.focus();
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
watch(isLoading, async (loading) => {
  if (!loading) {
    await nextTick();
    inputEl.value?.focus();
  }
});

// ---------------------------------------------------------------------------
// Place a call via the form
// ---------------------------------------------------------------------------
async function startCallFromComposer(payload: {
  personaId: string;
  personaLabel: string;
  personaInstructions: string;
  customerName: string;
  countryCode: string;
  phoneNumber: string;
  verificationFacts: string;
  notes: string;
  language: string;
  languageCode: string;
}) {
  callError.value = null;
  try {
    const res = await $fetch<{
      success: boolean;
      contextId?: string;
      phoneNumber?: string;
      error?: string;
    }>("/api/place-call", {
      method: "POST",
      body: payload,
    });

    if (!res.success || !res.contextId) {
      callError.value = res.error || "Ukendt fejl";
      return;
    }

    addSession({
      contextId: res.contextId,
      customerName: payload.customerName,
      phoneNumber: res.phoneNumber || payload.phoneNumber,
      personaLabel: payload.personaLabel,
      direction: "outbound",
    });
  } catch (err: unknown) {
    callError.value = err instanceof Error ? err.message : String(err);
  }
}

// Auto-collapse all but the newest active session when a new call starts
watch(
  () => sessions.value.length,
  (newLen, oldLen) => {
    if (newLen > oldLen && newLen > 1) {
      const newest = sessions.value[sessions.value.length - 1];
      sessions.value.forEach((s) => {
        if (s.contextId !== newest.contextId && s.status !== "ended") {
          s.expanded = false;
        }
      });
    }
  },
);

const activeCount = computed(
  () => sessions.value.filter((s) => s.status !== "ended").length,
);
</script>

<template>
  <div class="flex h-dvh w-screen bg-norlys-sand text-norlys-ink font-body">
    <!-- ============== TOP BAR ============== -->
    <!-- Per Norlys CVI: logo sits in the top-right corner of the layout. -->
    <header
      class="fixed top-0 inset-x-0 h-12 z-30 bg-white border-b border-norlys-light-petroleum flex items-center px-4 gap-4"
    >
      <!-- Official Norlys logo (Norlys_Logotype_RGB.svg, used with permission) -->
      <img
        src="/norlys-logo.svg"
        alt="Norlys"
        class="h-6 w-auto select-none"
        draggable="false"
      />
      <div
        v-if="activeCount > 0"
        class="hidden sm:flex items-center gap-1.5 text-xs text-norlys-petroleum"
      >
        <span class="w-1.5 h-1.5 rounded-full bg-norlys-red animate-pulse" />
        {{ activeCount }} aktivt opkald
      </div>
      <div class="flex-1" />
      <span
        class="font-headline text-base font-bold text-norlys-petroleum-3 hidden sm:inline"
        >CallCenter</span
      >
    </header>

    <!-- ============== LEFT — CALL COMPOSER ============== -->
    <div class="hidden lg:flex pt-12 w-80 shrink-0 h-full">
      <CallComposer @call="startCallFromComposer" />
    </div>

    <!-- ============== CENTER — CHAT ============== -->
    <div class="flex-1 flex flex-col h-full min-w-0 pt-12">
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
            class="flex flex-col items-center text-center gap-4"
          >
            <div
              class="w-12 h-12 rounded-full bg-norlys-red flex items-center justify-center"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                class="w-6 h-6 text-norlys-sand"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2.2"
                stroke-linecap="round"
                stroke-linejoin="round"
              >
                <path
                  d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
                />
              </svg>
            </div>
            <div class="flex flex-col items-center max-w-md">
              <h1
                class="font-headline text-3xl font-bold text-norlys-petroleum-3 text-center"
              >
                CallCenter Demo
              </h1>
              <p class="text-sm text-norlys-ink/70 mt-1 text-center">
                Start et opkald fra panelet til venstre, eller bed assistenten
                herunder om hjælp.
              </p>
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

          <!-- Errors -->
          <div v-if="error" class="text-center text-norlys-red text-sm py-2">
            {{ error }}
          </div>
          <div
            v-if="callError"
            class="text-center text-norlys-red text-sm py-2"
          >
            Opkaldsfejl: {{ callError }}
          </div>
        </div>
      </div>

      <!-- Clear chat -->
      <div
        v-if="messages.length > 0"
        class="flex justify-center px-4 pb-2 max-w-3xl mx-auto w-full"
      >
        <button
          class="inline-flex items-center gap-1.5 px-4 py-1.5 rounded-full border border-norlys-light-petroleum text-norlys-petroleum text-xs hover:bg-norlys-sand-2 transition-colors"
          @click="clearMessages()"
        >
          Ryd samtale
        </button>
      </div>

      <!-- Input -->
      <div
        class="px-4 py-3 max-w-3xl mx-auto w-full pb-[max(0.75rem,env(safe-area-inset-bottom))]"
      >
        <div
          class="flex items-center gap-3 border border-norlys-light-petroleum rounded-full px-5 py-3 bg-white shadow-sm focus-within:border-norlys-red focus-within:ring-1 focus-within:ring-norlys-red/20 transition-all"
        >
          <input
            ref="inputEl"
            v-model="input"
            type="text"
            placeholder="Spørg assistenten…"
            class="flex-1 bg-transparent text-base text-norlys-ink placeholder:text-norlys-petroleum/50 focus:outline-none font-body"
            @keydown.enter.prevent="send"
          />
          <button
            v-if="isLoading"
            class="shrink-0 w-8 h-8 flex items-center justify-center rounded-full bg-norlys-petroleum hover:bg-norlys-petroleum-3 text-norlys-sand transition-colors"
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
                ? 'bg-norlys-red text-norlys-sand hover:bg-norlys-red-3'
                : 'bg-norlys-light-petroleum text-norlys-petroleum/40 cursor-default'
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

    <!-- ============== RIGHT — CALL SESSIONS ============== -->
    <aside
      class="hidden lg:flex flex-col w-[28rem] xl:w-[32rem] shrink-0 h-full pt-12 border-l border-norlys-light-petroleum bg-white"
    >
      <div
        class="px-4 py-3 border-b border-norlys-light-petroleum flex items-center justify-between"
      >
        <div class="flex items-center gap-2">
          <span
            class="font-headline text-base font-bold text-norlys-petroleum-3"
            >Aktive opkald</span
          >
          <span
            class="inline-flex items-center justify-center min-w-[20px] h-5 px-1.5 rounded-full bg-norlys-red text-norlys-sand text-[10px] font-bold"
            >{{ sessions.length }}</span
          >
        </div>
        <button
          v-if="sessions.some((s) => s.status === 'ended')"
          type="button"
          class="text-[11px] text-norlys-petroleum hover:text-norlys-red transition-colors"
          @click="clearEnded()"
        >
          Ryd afsluttede
        </button>
      </div>

      <div class="flex-1 overflow-y-auto p-3 space-y-2">
        <div
          v-if="sessions.length === 0"
          class="flex flex-col items-center justify-center h-full text-center text-norlys-petroleum/60 px-6"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="w-10 h-10 text-norlys-light-petroleum mb-3"
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
          <p class="text-sm font-medium text-norlys-petroleum">
            Ingen opkald endnu
          </p>
          <p class="text-xs text-norlys-petroleum/60 mt-1">
            Vælg et scenarie og udfyld formularen til venstre.
          </p>
        </div>

        <CallSessionCard
          v-for="s in [...sessions].reverse()"
          :key="s.contextId"
          :session="s"
          @toggle="toggleExpanded(s.contextId)"
          @remove="removeSession(s.contextId)"
        />
      </div>
    </aside>

    <!-- Single version badge (bottom-right) — git commit count -->
    <div
      class="fixed bottom-1 right-2 z-30 text-[10px] font-semibold tabular-nums text-norlys-petroleum/50 select-none pointer-events-none"
      :title="`Version ${config.public.appVersion}`"
    >
      v{{ config.public.buildNumber }}
    </div>
  </div>
</template>
