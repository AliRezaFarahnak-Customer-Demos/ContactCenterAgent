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
</script>

<template>
  <aside
    class="w-full h-full flex flex-col border-r border-zinc-200 bg-white overflow-hidden"
  >
    <!-- Header -->
    <div class="px-4 py-3 border-b border-zinc-100 flex items-center gap-2">
      <svg
        xmlns="http://www.w3.org/2000/svg"
        class="w-4 h-4 text-[#ED0812]"
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
      <span class="text-sm font-semibold text-zinc-800">Ny opringning</span>
    </div>

    <!-- Form -->
    <div class="flex-1 overflow-y-auto px-4 py-4 space-y-4">
      <!-- Persona -->
      <div>
        <label
          class="text-xs font-semibold text-zinc-500 uppercase tracking-wide"
          >Scenarie</label
        >
        <div class="mt-1.5 grid gap-2">
          <button
            v-for="p in CALL_PERSONAS"
            :key="p.id"
            type="button"
            class="text-left px-3 py-2.5 rounded-lg border transition-all"
            :class="
              personaId === p.id
                ? 'border-[#ED0812] bg-red-50/60 ring-1 ring-[#ED0812]/20'
                : 'border-zinc-200 hover:border-zinc-300 bg-white'
            "
            @click="personaId = p.id"
          >
            <div class="flex items-center gap-2">
              <span class="text-base">{{ p.emoji }}</span>
              <span class="text-sm font-semibold text-zinc-800">{{
                p.label
              }}</span>
            </div>
            <p class="text-[11px] text-zinc-500 mt-1 leading-snug">
              {{ p.description }}
            </p>
          </button>
        </div>
      </div>

      <!-- Customer name -->
      <div>
        <label
          class="text-xs font-semibold text-zinc-500 uppercase tracking-wide"
          >Kundenavn</label
        >
        <input
          v-model="customerName"
          type="text"
          placeholder="fx Mette Hansen"
          class="mt-1.5 w-full px-3 py-2 text-sm border border-zinc-200 rounded-lg focus:outline-none focus:border-[#ED0812] focus:ring-1 focus:ring-[#ED0812]/30"
        />
      </div>

      <!-- Phone number -->
      <div>
        <div class="flex items-center justify-between">
          <label
            class="text-xs font-semibold text-zinc-500 uppercase tracking-wide"
            >Telefonnummer</label
          >
          <!-- Animated hint when empty -->
          <span
            v-if="!phoneNumber.trim()"
            class="inline-flex items-center gap-1 text-[10px] font-semibold text-[#ED0812] cc-arrow-bounce"
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
            >
              <line x1="12" y1="5" x2="12" y2="19" />
              <polyline points="19 12 12 19 5 12" />
            </svg>
          </span>
        </div>
        <div class="mt-1.5 flex gap-2">
          <div
            class="flex items-center px-2.5 py-2 text-sm border border-zinc-200 rounded-lg bg-zinc-50 text-zinc-600 font-mono"
          >
            +<input
              v-model="countryCode"
              type="text"
              maxlength="3"
              class="w-7 bg-transparent focus:outline-none"
            />
          </div>
          <input
            v-model="phoneNumber"
            type="tel"
            placeholder="80 71 90 50"
            class="flex-1 px-3 py-2 text-sm border rounded-lg focus:outline-none focus:border-[#ED0812] focus:ring-1 focus:ring-[#ED0812]/30 font-mono transition-shadow"
            :class="
              !phoneNumber.trim()
                ? 'border-[#ED0812]/50 ring-2 ring-[#ED0812]/15 cc-input-pulse'
                : 'border-zinc-200'
            "
          />
        </div>
      </div>

      <!-- Verification facts -->
      <div>
        <label
          class="text-xs font-semibold text-zinc-500 uppercase tracking-wide flex items-center gap-1.5"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            class="w-3.5 h-3.5 text-[#ED0812]"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <rect width="18" height="11" x="3" y="11" rx="2" ry="2" />
            <path d="M7 11V7a5 5 0 0 1 10 0v4" />
          </svg>
          MFA — verifikationsfakta
        </label>
        <textarea
          v-model="verificationFacts"
          rows="3"
          placeholder="Adresse: ...&#10;Email: ...&#10;Fødselsdato: ..."
          class="mt-1.5 w-full px-3 py-2 text-sm border border-zinc-200 rounded-lg focus:outline-none focus:border-[#ED0812] focus:ring-1 focus:ring-[#ED0812]/30 font-mono resize-none"
        ></textarea>
        <p class="text-[10px] text-zinc-400 mt-1 leading-snug">
          Agenten stiller spørgsmål baseret på disse fakta før han diskuterer
          noget følsomt.
        </p>
      </div>

      <!-- Notes -->
      <div>
        <label
          class="text-xs font-semibold text-zinc-500 uppercase tracking-wide"
          >Kontekst (valgfrit)</label
        >
        <textarea
          v-model="notes"
          rows="2"
          placeholder="fx detaljer om kundens ordre, regningsbeløb, ..."
          class="mt-1.5 w-full px-3 py-2 text-sm border border-zinc-200 rounded-lg focus:outline-none focus:border-[#ED0812] focus:ring-1 focus:ring-[#ED0812]/30 resize-none"
        ></textarea>
      </div>

      <!-- Advanced — full prompt -->
      <details
        class="border border-zinc-200 rounded-lg"
        @toggle="showAdvanced = ($event.target as HTMLDetailsElement).open"
      >
        <summary
          class="px-3 py-2 text-xs font-semibold text-zinc-500 cursor-pointer select-none uppercase tracking-wide"
        >
          Avanceret — instruks-skabelon
        </summary>
        <textarea
          v-model="personaInstructions"
          rows="10"
          class="w-full px-3 py-2 text-xs border-t border-zinc-200 focus:outline-none font-mono resize-none rounded-b-lg"
        ></textarea>
      </details>
    </div>

    <!-- Call button -->
    <div class="px-4 py-3 border-t border-zinc-100 bg-white">
      <button
        type="button"
        :disabled="!canCall"
        class="w-full inline-flex items-center justify-center gap-2 px-4 py-3 rounded-full text-white text-sm font-bold transition-all duration-200"
        :class="
          canCall
            ? 'bg-[#ED0812] hover:bg-[#c4060f] active:scale-[0.98] shadow-md'
            : 'bg-zinc-300 cursor-not-allowed'
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
        >
          <path
            d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z"
          />
        </svg>
        {{ isCalling ? "Ringer op…" : "Ring op nu" }}
      </button>
      <p v-if="lastError" class="mt-2 text-xs text-[#ED0812] text-center">
        {{ lastError }}
      </p>
      <p class="mt-2 text-[10px] text-zinc-400 text-center leading-snug">
        Du kan starte flere opkald samtidig — hvert vises som en sektion til
        højre.
      </p>
    </div>
  </aside>
</template>
