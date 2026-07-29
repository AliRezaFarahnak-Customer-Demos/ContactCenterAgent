<script setup lang="ts">
/**
 * CallComposer — simplified demo UI.
 *
 * One textarea = the entire system prompt (no separate name / MFA / notes
 * fields). Switching scenarios swaps the prompt. Click Call → emit current
 * values verbatim. Reload = reset to defaults.
 */
import {
    loadPersonas,
    usePersonas,
    type CallPersona,
} from "~/composables/useCallPersonas";

const emit = defineEmits<{
  call: [
    payload: {
      personaId: string;
      personaLabel: string;
      countryCode: string;
      phoneNumber: string;
      prompt: string;
      language: string;
      languageCode: string;
      voice: string;
      voiceStyle: string;
    },
  ];
}>();

type VoiceOption = {
  value: string;
  label: string;
  description: string;
  hd: boolean;
};
type StyleOption = { value: string; label: string; documented: boolean };

const voices = ref<VoiceOption[]>([]);
const styles = ref<StyleOption[]>([]);
const selectedVoice = ref("");
const selectedStyle = ref("");
const showVoiceOptions = ref(false);

// Style is an HD-only field; standard neural voices ignore it and unknown fields
// on them can trigger a silent fallback to a default voice.
const selectedVoiceIsHd = computed(
  () => voices.value.find((v) => v.value === selectedVoice.value)?.hd ?? true,
);

const personas = usePersonas();
const personaId = ref<string>("");
const isLoading = ref(true);
const isCalling = ref(false);
const lastError = ref<string | null>(null);

const persona = computed<CallPersona | undefined>(() =>
  personas.value.find((p) => p.id === personaId.value),
);

const countryCode = ref("");
const phoneNumber = ref("");
const promptText = ref("");

function hydrateFromPersona(p: CallPersona) {
  countryCode.value = p.countryCode;
  phoneNumber.value = p.phoneNumber;
  promptText.value = p.prompt;
}

watch(personaId, (newId) => {
  const p = personas.value.find((x) => x.id === newId);
  if (p) hydrateFromPersona(p);
});

onMounted(async () => {
  try {
    const list = await loadPersonas();
    if (list.length > 0) {
      personaId.value = list[0].id;
      hydrateFromPersona(list[0]);
    }
  } catch (err) {
    lastError.value =
      err instanceof Error ? err.message : "Kunne ikke hente personas";
  } finally {
    isLoading.value = false;
  }

  // Non-blocking: a failure here just leaves the agent defaults in play.
  try {
    const opts = await $fetch<{
      defaultVoice: string;
      defaultStyle: string;
      voices: VoiceOption[];
      styles: StyleOption[];
    }>("/api/voice-options");
    voices.value = opts.voices;
    styles.value = opts.styles;
    selectedVoice.value = opts.defaultVoice;
    selectedStyle.value = opts.defaultStyle;
  } catch {
    /* keep agent-side defaults */
  }
});

const canCall = computed(
  () =>
    !isCalling.value &&
    !isLoading.value &&
    !!persona.value &&
    promptText.value.trim().length > 0 &&
    phoneNumber.value.replace(/\D/g, "").length >= 6,
);

async function startCall() {
  if (!canCall.value || !persona.value) return;
  isCalling.value = true;
  lastError.value = null;
  try {
    emit("call", {
      personaId: persona.value.id,
      personaLabel: persona.value.label,
      countryCode: countryCode.value,
      phoneNumber: phoneNumber.value,
      prompt: promptText.value,
      language: persona.value.language,
      languageCode: persona.value.languageCode,
      voice: selectedVoice.value,
      voiceStyle: selectedVoiceIsHd.value ? selectedStyle.value : "",
    });
  } finally {
    setTimeout(() => {
      isCalling.value = false;
    }, 800);
  }
}

function resizeTextarea(el: HTMLTextAreaElement) {
  el.style.height = "auto";
  el.style.height = `${el.scrollHeight}px`;
}
const vAutosize = {
  mounted(el: HTMLTextAreaElement) {
    el.style.overflow = "hidden";
    el.addEventListener("input", () => resizeTextarea(el));
    nextTick(() => resizeTextarea(el));
  },
  updated(el: HTMLTextAreaElement) {
    nextTick(() => resizeTextarea(el));
  },
};

/**
 * Parse the SECURITY QUESTIONS block at the top of the prompt into a list of
 * { label, value } pairs so the UI can show them as a cheat sheet — handy
 * when demoing, since these are the values the customer must speak to pass
 * the AI's identity check. Block format:
 *
 *   SECURITY QUESTIONS
 *   Adresse: Hovedgaden 12
 *   Email: kunde@example.dk
 *   END OF SECURITY QUESTIONS
 *
 * Delimiters are case-insensitive and trimmed. Lines without ":" are ignored.
 */
const verificationFacts = computed<Array<{ label: string; value: string }>>(
  () => {
    const text = promptText.value;
    if (!text) return [];
    const lines = text.split(/\r?\n/);
    const startIdx = lines.findIndex(
      (l) => l.trim().toUpperCase() === "SECURITY QUESTIONS",
    );
    if (startIdx === -1) return [];
    const endIdx = lines.findIndex(
      (l, i) =>
        i > startIdx && l.trim().toUpperCase() === "END OF SECURITY QUESTIONS",
    );
    const slice = lines.slice(
      startIdx + 1,
      endIdx === -1 ? lines.length : endIdx,
    );
    return (
      slice
        .map((line) => line.trim())
        .filter((line) => line && line.includes(":"))
        .map((line) => {
          const idx = line.indexOf(":");
          return {
            label: line.slice(0, idx).trim(),
            value: line.slice(idx + 1).trim(),
          };
        })
        // "Navn" is just for greeting (the AI says it out loud) — not a secret.
        // Hide it from the cheat-sheet so the operator sees only the actual
        // verification questions the customer must answer.
        .filter((f) => f.label.toLowerCase() !== "navn")
    );
  },
);
</script>

<template>
  <aside
    class="w-full h-full flex flex-col bg-white rounded-xl overflow-hidden"
  >
    <!-- Header -->
    <div class="px-4 py-3 flex items-center gap-2">
      <svg
        xmlns="http://www.w3.org/2000/svg"
        class="w-4 h-4 text-norlys-red"
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
      <span class="font-headline text-lg font-bold text-norlys-petroleum-3"
        >Nyt agent-opkald</span
      >
    </div>

    <div
      v-if="isLoading"
      class="flex-1 flex items-center justify-center text-sm text-norlys-petroleum/60"
    >
      Indlæser…
    </div>

    <div v-else class="flex-1 overflow-y-auto px-5 py-5 space-y-5">
      <!-- Persona -->
      <div>
        <label
          class="text-sm font-semibold text-norlys-petroleum uppercase tracking-wide"
          >Scenarie</label
        >
        <div class="mt-2 grid gap-2">
          <button
            v-for="p in personas"
            :key="p.id"
            type="button"
            class="text-left px-4 py-3 rounded-lg transition-all focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40"
            :class="
              personaId === p.id
                ? 'bg-norlys-red/5 ring-1 ring-norlys-red/30'
                : 'bg-norlys-sand/60 hover:bg-norlys-sand-2'
            "
            @click="personaId = p.id"
          >
            <div class="flex items-center gap-2">
              <span class="text-lg" aria-hidden="true">{{ p.emoji }}</span>
              <span
                class="font-headline text-base font-bold text-norlys-petroleum-3"
                >{{ p.label }}</span
              >
            </div>
            <p class="text-sm text-norlys-ink/70 mt-1 leading-snug">
              {{ p.description }}
            </p>
          </button>
        </div>
      </div>

      <!-- Phone number -->
      <div>
        <div class="flex items-center justify-between">
          <label
            for="composer-phone-number"
            class="text-sm font-semibold text-norlys-petroleum uppercase tracking-wide"
            >Telefonnummer</label
          >
          <span
            v-if="!phoneNumber.trim()"
            class="inline-flex items-center gap-1 text-xs font-semibold text-norlys-red cc-arrow-bounce"
          >
            Indtast nummer
            <svg
              xmlns="http://www.w3.org/2000/svg"
              class="w-3.5 h-3.5"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2.5"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <line x1="12" y1="5" x2="12" y2="19" />
              <polyline points="19 12 12 19 5 12" />
            </svg>
          </span>
        </div>
        <div class="mt-2 flex gap-2">
          <div
            class="flex items-center px-3 py-2.5 text-base rounded-lg bg-norlys-sand-2 text-norlys-petroleum tabular-nums"
          >
            +<input
              v-model="countryCode"
              type="text"
              maxlength="3"
              aria-label="Landekode"
              class="w-8 bg-transparent focus:outline-none"
            />
          </div>
          <input
            id="composer-phone-number"
            v-model="phoneNumber"
            type="tel"
            placeholder="80 71 90 50"
            class="flex-1 px-3 py-2.5 text-base rounded-lg bg-norlys-sand/60 text-norlys-ink focus:outline-none focus:bg-white focus:ring-2 focus:ring-norlys-red/30 tabular-nums transition-all"
            :class="
              !phoneNumber.trim()
                ? 'ring-2 ring-norlys-red/25 cc-input-pulse'
                : ''
            "
          />
        </div>
      </div>

      <!-- Voice options (collapsed by default — sensible defaults already applied) -->
      <div v-if="voices.length > 0">
        <button
          type="button"
          class="w-full flex items-center justify-between text-sm font-semibold text-norlys-petroleum uppercase tracking-wide focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40 rounded"
          :aria-expanded="showVoiceOptions"
          @click="showVoiceOptions = !showVoiceOptions"
        >
          <span>Stemme</span>
          <span
            class="flex items-center gap-1.5 normal-case tracking-normal font-normal text-norlys-ink/60"
          >
            {{ voices.find((v) => v.value === selectedVoice)?.label }}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              class="w-4 h-4 transition-transform"
              :class="showVoiceOptions ? 'rotate-180' : ''"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <polyline points="6 9 12 15 18 9" />
            </svg>
          </span>
        </button>

        <div v-if="showVoiceOptions" class="mt-2 space-y-3">
          <div class="grid gap-1.5">
            <button
              v-for="v in voices"
              :key="v.value"
              type="button"
              class="text-left px-3 py-2 rounded-lg transition-all focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40"
              :class="
                selectedVoice === v.value
                  ? 'bg-norlys-red/5 ring-1 ring-norlys-red/30'
                  : 'bg-norlys-sand/60 hover:bg-norlys-sand-2'
              "
              @click="selectedVoice = v.value"
            >
              <div class="flex items-center gap-2">
                <span class="text-sm font-semibold text-norlys-petroleum-3">{{
                  v.label
                }}</span>
                <span
                  v-if="v.hd"
                  class="text-[10px] px-1.5 py-0.5 rounded bg-norlys-petroleum/10 text-norlys-petroleum font-semibold"
                  >HD</span
                >
              </div>
              <p class="text-xs text-norlys-ink/60 mt-0.5 leading-snug">
                {{ v.description }}
              </p>
            </button>
          </div>

          <div v-if="selectedVoiceIsHd">
            <span
              class="text-xs font-semibold text-norlys-petroleum uppercase tracking-wide"
              >Tone</span
            >
            <div class="mt-1.5 flex flex-wrap gap-1.5">
              <button
                v-for="s in styles"
                :key="s.value"
                type="button"
                class="px-2.5 py-1 rounded-full text-xs transition-all focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40"
                :class="
                  selectedStyle === s.value
                    ? 'bg-norlys-red text-white'
                    : 'bg-norlys-sand-2 text-norlys-petroleum hover:bg-norlys-light-petroleum'
                "
                :title="
                  s.documented
                    ? 'Dokumenteret HD Omni-stil'
                    : 'Ikke i den officielle HD Omni-liste'
                "
                @click="selectedStyle = s.value"
              >
                {{ s.label
                }}<span v-if="!s.documented" aria-hidden="true">&nbsp;*</span>
              </button>
            </div>
            <p class="text-[11px] text-norlys-ink/50 mt-1.5 leading-snug">
              * ikke i den officielle HD Omni-liste — virker, men udokumenteret.
            </p>
          </div>
          <p v-else class="text-[11px] text-norlys-ink/50 leading-snug">
            Standardstemmer understøtter ikke tone eller temperatur.
          </p>
        </div>
      </div>

      <!-- Verifikationsfakta cheat sheet (parsed from prompt) -->
      <div
        v-if="verificationFacts.length > 0"
        class="rounded-lg bg-amber-50 px-4 py-3"
      >
        <div class="flex items-center gap-1.5 mb-2">
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="w-4 h-4 text-amber-700"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <rect width="18" height="11" x="3" y="11" rx="2" ry="2" />
            <path d="M7 11V7a5 5 0 0 1 10 0v4" />
          </svg>
          <span
            class="text-xs font-semibold uppercase tracking-wide text-amber-800"
            >Demo: hemmelige værdier kunden skal sige</span
          >
        </div>
        <ul class="space-y-1">
          <li
            v-for="f in verificationFacts"
            :key="f.label"
            class="flex items-baseline gap-2 text-sm"
          >
            <span class="text-amber-800 font-medium min-w-16"
              >{{ f.label }}:</span
            >
            <span
              class="tabular-nums font-semibold text-amber-900 select-all"
              >{{ f.value }}</span
            >
          </li>
        </ul>
      </div>

      <!-- System prompt (single source of truth) -->
      <div>
        <label
          for="composer-prompt"
          class="text-sm font-semibold text-norlys-petroleum uppercase tracking-wide"
          >System prompt</label
        >
        <textarea
          id="composer-prompt"
          v-model="promptText"
          v-autosize
          rows="14"
          aria-label="System prompt"
          class="mt-2 w-full px-3 py-3 text-sm rounded-lg bg-norlys-sand/60 text-norlys-ink focus:outline-none focus:bg-white focus:ring-2 focus:ring-norlys-red/30 resize-none tabular-nums leading-relaxed transition-all"
        ></textarea>
      </div>
    </div>

    <!-- Call button -->
    <div class="px-5 py-4 bg-white">
      <button
        type="button"
        :disabled="!canCall"
        class="w-full inline-flex items-center justify-center gap-2 px-4 py-3.5 rounded-full text-norlys-sand font-body text-base font-bold transition-all duration-200 focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40 focus-visible:ring-offset-2 focus-visible:ring-offset-white"
        :class="
          canCall
            ? 'bg-norlys-red hover:bg-norlys-red-3 active:scale-[0.98] shadow-md'
            : 'bg-norlys-light-petroleum-3 cursor-not-allowed'
        "
        @click="startCall"
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
          aria-hidden="true"
        >
          <path
            d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
          />
        </svg>
        {{ isCalling ? "Ringer op…" : "Ring op med Agent" }}
      </button>
      <p v-if="lastError" class="mt-2 text-xs text-norlys-red text-center">
        {{ lastError }}
      </p>
      <p
        class="mt-2 text-[10px] text-norlys-petroleum/60 text-center leading-snug"
      >
        Du kan starte flere opkald samtidig — hvert vises som en sektion til
        højre.
      </p>
    </div>
  </aside>
</template>
