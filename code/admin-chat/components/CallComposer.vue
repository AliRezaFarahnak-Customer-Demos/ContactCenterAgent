<script setup lang="ts">
import { CALL_PERSONAS, getPersona } from "~/composables/useCallPersonas";

const emit = defineEmits<{
  call: [
    payload: {
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
    },
  ];
}>();

const personaId = ref(CALL_PERSONAS[0].id);
const persona = computed(() => getPersona(personaId.value) ?? CALL_PERSONAS[0]);

const customerName = ref("Mette Hansen");
const countryCode = ref("45");
const phoneNumber = ref("");
const verificationFacts = ref(persona.value.defaultVerificationFacts);
const notes = ref(persona.value.defaultNotes);
const personaInstructions = ref(persona.value.instructions);
const showAdvanced = ref(false);
const isCalling = ref(false);
const lastError = ref<string | null>(null);

// When persona changes, refresh templates (but only if untouched defaults)
watch(personaId, (newId, oldId) => {
  const oldP = getPersona(oldId);
  const newP = getPersona(newId);
  if (!newP) return;
  if (!oldP || verificationFacts.value === oldP.defaultVerificationFacts) {
    verificationFacts.value = newP.defaultVerificationFacts;
  }
  if (!oldP || notes.value === oldP.defaultNotes) {
    notes.value = newP.defaultNotes;
  }
  if (!oldP || personaInstructions.value === oldP.instructions) {
    personaInstructions.value = newP.instructions;
  }
});

const canCall = computed(
  () =>
    !isCalling.value &&
    customerName.value.trim().length > 0 &&
    phoneNumber.value.replace(/\D/g, "").length >= 6,
);

async function startCall() {
  if (!canCall.value) return;
  isCalling.value = true;
  lastError.value = null;
  try {
    emit("call", {
      personaId: personaId.value,
      personaLabel: persona.value.label,
      personaInstructions: personaInstructions.value,
      customerName: customerName.value.trim(),
      countryCode: countryCode.value,
      phoneNumber: phoneNumber.value,
      verificationFacts: verificationFacts.value,
      notes: notes.value,
      language: persona.value.language,
      languageCode: persona.value.languageCode,
    });
  } finally {
    // Re-enable button shortly after — parent handles real lifecycle
    setTimeout(() => {
      isCalling.value = false;
    }, 800);
  }
}

// Auto-grow textarea directive: resize element to fit its content.
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
</script>

<template>
  <aside
    class="w-full h-full flex flex-col border-r border-norlys-light-petroleum bg-white overflow-hidden"
  >
    <!-- Header -->
    <div
      class="px-4 py-3 border-b border-norlys-light-petroleum flex items-center gap-2"
    >
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
      <span class="font-headline text-base font-bold text-norlys-petroleum-3"
        >Ny opringning</span
      >
    </div>

    <!-- Form -->
    <div class="flex-1 overflow-y-auto px-4 py-4 space-y-4">
      <!-- Persona -->
      <div>
        <label
          class="text-xs font-semibold text-norlys-petroleum uppercase tracking-wide"
          >Scenarie</label
        >
        <div class="mt-1.5 grid gap-2">
          <button
            v-for="p in CALL_PERSONAS"
            :key="p.id"
            type="button"
            class="text-left px-3 py-2.5 rounded-lg border transition-all focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40"
            :class="
              personaId === p.id
                ? 'border-norlys-red bg-norlys-red/5 ring-1 ring-norlys-red/20'
                : 'border-norlys-light-petroleum hover:border-norlys-petroleum/40 bg-white'
            "
            @click="personaId = p.id"
          >
            <div class="flex items-center gap-2">
              <span class="text-base" aria-hidden="true">{{ p.emoji }}</span>
              <span
                class="font-headline text-sm font-bold text-norlys-petroleum-3"
                >{{ p.label }}</span
              >
            </div>
            <p class="text-[11px] text-norlys-ink/70 mt-1 leading-snug">
              {{ p.description }}
            </p>
          </button>
        </div>
      </div>

      <!-- Customer name -->
      <div>
        <label
          for="composer-customer-name"
          class="text-xs font-semibold text-norlys-petroleum uppercase tracking-wide"
          >Kundenavn</label
        >
        <input
          id="composer-customer-name"
          v-model="customerName"
          type="text"
          placeholder="fx Mette Hansen"
          class="mt-1.5 w-full px-3 py-2 text-sm border border-norlys-light-petroleum rounded-lg bg-white text-norlys-ink focus:outline-none focus:border-norlys-red focus:ring-1 focus:ring-norlys-red/30"
        />
      </div>

      <!-- Phone number -->
      <div>
        <div class="flex items-center justify-between">
          <label
            for="composer-phone-number"
            class="text-xs font-semibold text-norlys-petroleum uppercase tracking-wide"
            >Telefonnummer</label
          >
          <!-- Animated hint when empty -->
          <span
            v-if="!phoneNumber.trim()"
            class="inline-flex items-center gap-1 text-[10px] font-semibold text-norlys-red cc-arrow-bounce"
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
        <div class="mt-1.5 flex gap-2">
          <div
            class="flex items-center px-2.5 py-2 text-sm border border-norlys-light-petroleum rounded-lg bg-norlys-sand-2 text-norlys-petroleum tabular-nums"
          >
            +<input
              v-model="countryCode"
              type="text"
              maxlength="3"
              aria-label="Landekode"
              class="w-7 bg-transparent focus:outline-none"
            />
          </div>
          <input
            id="composer-phone-number"
            v-model="phoneNumber"
            type="tel"
            placeholder="80 71 90 50"
            class="flex-1 px-3 py-2 text-sm border rounded-lg bg-white text-norlys-ink focus:outline-none focus:border-norlys-red focus:ring-1 focus:ring-norlys-red/30 tabular-nums transition-shadow"
            :class="
              !phoneNumber.trim()
                ? 'border-norlys-red/50 ring-2 ring-norlys-red/15 cc-input-pulse'
                : 'border-norlys-light-petroleum'
            "
          />
        </div>
      </div>

      <!-- Verification facts -->
      <div>
        <label
          for="composer-verification"
          class="text-xs font-semibold text-norlys-petroleum uppercase tracking-wide flex items-center gap-1.5"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="w-3.5 h-3.5 text-norlys-red"
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
          MFA — verifikationsfakta
        </label>
        <textarea
          id="composer-verification"
          v-model="verificationFacts"
          v-autosize
          rows="3"
          placeholder="Adresse: ...&#10;Email: ...&#10;Fødselsdato: ..."
          class="mt-1.5 w-full px-3 py-2 text-sm border border-norlys-light-petroleum rounded-lg bg-white text-norlys-ink focus:outline-none focus:border-norlys-red focus:ring-1 focus:ring-norlys-red/30 resize-none"
        ></textarea>
        <p class="text-[10px] text-norlys-petroleum/60 mt-1 leading-snug">
          Agenten stiller spørgsmål baseret på disse fakta før han diskuterer
          noget følsomt.
        </p>
      </div>

      <!-- Notes -->
      <div>
        <label
          for="composer-notes"
          class="text-xs font-semibold text-norlys-petroleum uppercase tracking-wide"
          >Kontekst (valgfrit)</label
        >
        <textarea
          id="composer-notes"
          v-model="notes"
          v-autosize
          rows="2"
          placeholder="fx detaljer om kundens ordre, regningsbeløb, ..."
          class="mt-1.5 w-full px-3 py-2 text-sm border border-norlys-light-petroleum rounded-lg bg-white text-norlys-ink focus:outline-none focus:border-norlys-red focus:ring-1 focus:ring-norlys-red/30 resize-none"
        ></textarea>
      </div>

      <!-- Advanced — full prompt -->
      <details
        class="border border-norlys-light-petroleum rounded-lg"
        @toggle="showAdvanced = ($event.target as HTMLDetailsElement).open"
      >
        <summary
          class="px-3 py-2 text-xs font-semibold text-norlys-petroleum cursor-pointer select-none uppercase tracking-wide"
        >
          Avanceret — instruks-skabelon
        </summary>
        <textarea
          v-model="personaInstructions"
          v-autosize
          rows="10"
          aria-label="Instruks-skabelon"
          class="w-full px-3 py-2 text-xs border-t border-norlys-light-petroleum bg-white text-norlys-ink focus:outline-none resize-none rounded-b-lg"
        ></textarea>
      </details>
    </div>

    <!-- Call button -->
    <div class="px-4 py-3 border-t border-norlys-light-petroleum bg-white">
      <button
        type="button"
        :disabled="!canCall"
        class="w-full inline-flex items-center justify-center gap-2 px-4 py-3 rounded-full text-norlys-sand font-body text-sm font-bold transition-all duration-200 focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40 focus-visible:ring-offset-2 focus-visible:ring-offset-white"
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
        {{ isCalling ? "Ringer op…" : "Ring op nu" }}
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
