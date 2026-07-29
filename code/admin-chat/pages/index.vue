<script setup lang="ts">
const { sessions, addSession, removeSession, clearEnded, toggleExpanded } =
  useCallSessions();

const config = useRuntimeConfig();
const callError = ref<string | null>(null);

// Inbound number customers can ring — served by the API so it's never hardcoded here.
const { data: inboundPhone } = useFetch<{
  configured: boolean;
  display: string;
  telHref: string;
}>("/api/phone-number");

// ---------------------------------------------------------------------------
// Place a call via the form
// ---------------------------------------------------------------------------
async function startCallFromComposer(payload: {
  personaId: string;
  personaLabel: string;
  countryCode: string;
  phoneNumber: string;
  prompt: string;
  language: string;
  languageCode: string;
  voice: string;
  voiceStyle: string;
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
      customerName: payload.personaLabel,
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
  <div
    class="flex h-dvh w-screen bg-norlys-sand text-norlys-ink font-body gap-3 p-3 pt-[3.75rem]"
  >
    <!-- ============== TOP BAR ============== -->
    <!-- Per Norlys CVI: logo sits in the top-right corner of the layout. -->
    <header
      class="fixed top-0 inset-x-0 h-12 z-30 bg-white flex items-center px-4 gap-4"
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
      <a
        v-if="inboundPhone?.configured"
        :href="inboundPhone.telHref"
        class="flex items-center gap-1.5 text-sm text-norlys-petroleum hover:text-norlys-red focus-visible:text-norlys-red transition-colors"
        :aria-label="`Ring til Norlys på ${inboundPhone.display}`"
      >
        <svg
          class="w-4 h-4 shrink-0"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path
            d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6A19.79 19.79 0 0 1 2.12 4.18 2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72c.13.96.36 1.9.7 2.81a2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45c.9.34 1.85.57 2.81.7A2 2 0 0 1 22 16.92z"
          />
        </svg>
        <span class="tabular-nums font-medium">{{ inboundPhone.display }}</span>
      </a>
      <div class="flex-1" />
      <span
        class="font-headline text-base font-bold text-norlys-petroleum-3 hidden sm:inline"
        >Agentic Call Center</span
      >
    </header>

    <!-- ============== LEFT — CALL COMPOSER ============== -->
    <div class="hidden lg:flex flex-1 min-w-0 h-full">
      <CallComposer @call="startCallFromComposer" />
    </div>

    <!-- ============== CENTER — CALL SESSIONS (sentiment UI) ============== -->
    <aside
      class="hidden lg:flex flex-col w-[28rem] xl:w-[34rem] 2xl:w-[40rem] shrink-0 h-full bg-white rounded-xl overflow-hidden"
    >
      <div class="px-4 py-3 flex items-center justify-between">
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

    <!-- ============== RIGHT — (chat panel removed; see copilot-instructions for restore notes) ============== -->
    <div
      v-if="callError"
      class="fixed top-16 left-1/2 -translate-x-1/2 z-30 px-4 py-2 rounded-lg bg-norlys-red text-norlys-sand text-sm shadow-md"
    >
      Opkaldsfejl: {{ callError }}
    </div>

    <!-- Single version badge (bottom-right) — git commit count -->
    <div
      class="fixed bottom-2 right-3 z-30 text-base font-bold tabular-nums text-norlys-petroleum select-none pointer-events-none"
      :title="`Version ${config.public.appVersion}`"
    >
      v{{ config.public.buildNumber }}
    </div>
  </div>
</template>
