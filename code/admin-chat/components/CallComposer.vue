<script setup lang="ts">
/**
 * CallComposer
 *
 * The persona prompt + ALL composer field defaults are the single source of
 * truth on the backend (/api/personas). On mount we load them. Whenever the
 * user types, we wait 1 second of inactivity and PUT the changes back, so a
 * page reload always shows exactly what was last typed.
 *
 * Only `personaId` + the per-call values (customer name, phone, fact / notes
 * overrides) are emitted upward. The full system prompt is resolved
 * server-side from the persona at call time.
 */
import {
    loadPersonas,
    savePersona,
    usePersonas,
    type CallPersona,
} from "~/composables/useCallPersonas";

const emit = defineEmits<{
  call: [
    payload: {
      personaId: string;
      personaLabel: string;
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

const personas = usePersonas();
const personaId = ref<string>("");
const isLoading = ref(true);
const isCalling = ref(false);
const lastError = ref<string | null>(null);
const saveStatus = ref<"idle" | "saving" | "saved">("idle");

const persona = computed<CallPersona | undefined>(() =>
  personas.value.find((p) => p.id === personaId.value),
);

const customerName = ref("");
const countryCode = ref("");
const phoneNumber = ref("");
const verificationFacts = ref("");
const notes = ref("");
const promptText = ref("");

// Suspend the auto-save watcher while we hydrate from a freshly loaded persona,
// otherwise we'd PUT back exactly what we just GOT.
let suspendSave = true;

function hydrateFromPersona(p: CallPersona) {
  suspendSave = true;
  customerName.value = p.customerName;
  countryCode.value = p.countryCode;
  phoneNumber.value = p.phoneNumber;
  verificationFacts.value = p.verificationFacts;
  notes.value = p.notes;
  promptText.value = p.prompt;
  nextTick(() => {
    suspendSave = false;
  });
}

// Debounced save: 1s of inactivity → PUT all editable fields.
let saveTimer: ReturnType<typeof setTimeout> | null = null;
async function flushSaveNow() {
  if (!persona.value) return;
  await savePersona(persona.value.id, {
    customerName: customerName.value,
    countryCode: countryCode.value,
    phoneNumber: phoneNumber.value,
    verificationFacts: verificationFacts.value,
    notes: notes.value,
    prompt: promptText.value,
  });
}
function scheduleSave() {
  if (suspendSave || !persona.value) return;
  saveStatus.value = "saving";
  if (saveTimer) clearTimeout(saveTimer);
  saveTimer = setTimeout(async () => {
    try {
      await flushSaveNow();
      saveStatus.value = "saved";
      setTimeout(() => {
        if (saveStatus.value === "saved") saveStatus.value = "idle";
      }, 1500);
    } catch (err) {
      saveStatus.value = "idle";
      lastError.value =
        err instanceof Error ? err.message : "Kunne ikke gemme ændringer";
    }
  }, 1000);
}

watch(
  [
    customerName,
    countryCode,
    phoneNumber,
    verificationFacts,
    notes,
    promptText,
  ],
  scheduleSave,
);

watch(personaId, async (newId, oldId) => {
  // Flush pending edits to the OLD persona before we overwrite the form.
  if (oldId && saveTimer) {
    clearTimeout(saveTimer);
    saveTimer = null;
    const stalePersona = personas.value.find((x) => x.id === oldId);
    if (stalePersona) {
      try {
        await savePersona(oldId, {
          customerName: customerName.value,
          countryCode: countryCode.value,
          phoneNumber: phoneNumber.value,
          verificationFacts: verificationFacts.value,
          notes: notes.value,
          prompt: promptText.value,
        });
      } catch {
        /* non-fatal */
      }
    }
  }
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
});

const canCall = computed(
  () =>
    !isCalling.value &&
    !isLoading.value &&
    !!persona.value &&
    customerName.value.trim().length > 0 &&
    phoneNumber.value.replace(/\D/g, "").length >= 6,
);

async function startCall() {
  if (!canCall.value || !persona.value) return;
  isCalling.value = true;
  lastError.value = null;

  // Flush any pending edit before placing the call so the backend resolves the
  // prompt with the latest text.
  if (saveTimer) {
    clearTimeout(saveTimer);
    saveTimer = null;
    try {
      await flushSaveNow();
    } catch {
      /* fall through — emit anyway */
    }
  }

  try {
    emit("call", {
      personaId: persona.value.id,
      personaLabel: persona.value.label,
      customerName: customerName.value.trim(),
      countryCode: countryCode.value,
      phoneNumber: phoneNumber.value,
      verificationFacts: verificationFacts.value,
      notes: notes.value,
      language: persona.value.language,
      languageCode: persona.value.languageCode,
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
      <span class="font-headline text-lg font-bold text-norlys-petroleum-3"
        >Ny opringning</span
      >
      <span
        v-if="saveStatus === 'saving'"
        class="ml-auto text-xs text-norlys-petroleum/60"
        >Gemmer…</span
      >
      <span
        v-else-if="saveStatus === 'saved'"
        class="ml-auto text-xs text-norlys-petroleum/60"
        >Gemt ✓</span
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
            class="text-left px-4 py-3 rounded-lg border transition-all focus:outline-none focus-visible:ring-2 focus-visible:ring-norlys-red/40"
            :class="
              personaId === p.id
                ? 'border-norlys-red bg-norlys-red/5 ring-1 ring-norlys-red/20'
                : 'border-norlys-light-petroleum hover:border-norlys-petroleum/40 bg-white'
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

      <!-- Customer name -->
      <div>
        <label
          for="composer-customer-name"
          class="text-sm font-semibold text-norlys-petroleum uppercase tracking-wide"
          >Kundenavn</label
        >
        <input
          id="composer-customer-name"
          v-model="customerName"
          type="text"
          placeholder="fx Mette Hansen"
          class="mt-2 w-full px-3 py-2.5 text-base border border-norlys-light-petroleum rounded-lg bg-white text-norlys-ink focus:outline-none focus:border-norlys-red focus:ring-1 focus:ring-norlys-red/30"
        />
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
            class="flex items-center px-3 py-2.5 text-base border border-norlys-light-petroleum rounded-lg bg-norlys-sand-2 text-norlys-petroleum tabular-nums"
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
            class="flex-1 px-3 py-2.5 text-base border rounded-lg bg-white text-norlys-ink focus:outline-none focus:border-norlys-red focus:ring-1 focus:ring-norlys-red/30 tabular-nums transition-shadow"
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
          class="text-sm font-semibold text-norlys-petroleum uppercase tracking-wide flex items-center gap-1.5"
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
          class="mt-2 w-full px-3 py-2.5 text-base border border-norlys-light-petroleum rounded-lg bg-white text-norlys-ink focus:outline-none focus:border-norlys-red focus:ring-1 focus:ring-norlys-red/30 resize-none"
        ></textarea>
        <p class="text-xs text-norlys-petroleum/60 mt-1 leading-snug">
          Agenten stiller spørgsmål baseret på disse fakta før han diskuterer
          noget følsomt.
        </p>
      </div>

      <!-- Notes -->
      <div>
        <label
          for="composer-notes"
          class="text-sm font-semibold text-norlys-petroleum uppercase tracking-wide"
          >Kontekst (valgfrit)</label
        >
        <textarea
          id="composer-notes"
          v-model="notes"
          v-autosize
          rows="2"
          placeholder="fx detaljer om kundens ordre, regningsbeløb, ..."
          class="mt-2 w-full px-3 py-2.5 text-base border border-norlys-light-petroleum rounded-lg bg-white text-norlys-ink focus:outline-none focus:border-norlys-red focus:ring-1 focus:ring-norlys-red/30 resize-none"
        ></textarea>
      </div>

      <!-- Advanced — full editable system prompt -->
      <details class="border border-norlys-light-petroleum rounded-lg">
        <summary
          class="px-4 py-3 text-sm font-semibold text-norlys-petroleum cursor-pointer select-none uppercase tracking-wide"
        >
          Avanceret — system-prompt
        </summary>
        <textarea
          v-model="promptText"
          v-autosize
          rows="16"
          aria-label="System-prompt"
          class="w-full px-3 py-3 text-sm border-t border-norlys-light-petroleum bg-white text-norlys-ink focus:outline-none resize-none rounded-b-lg font-mono leading-relaxed"
        ></textarea>
        <p
          v-pre
          class="px-3 py-2 text-xs text-norlys-petroleum/60 leading-snug border-t border-norlys-light-petroleum"
        >
          Pladsholdere:
          <code>{{ customerName }}</code
          >, <code>{{ phoneNumber }}</code
          >, <code>{{ personaLabel }}</code
          >, <code>{{ verificationFacts }}</code
          >, <code>{{ notes }}</code
          >, <code>{{ language }}</code
          >. Ændringer gemmes automatisk efter 1 sekund.
        </p>
      </details>
    </div>

    <!-- Call button -->
    <div class="px-5 py-4 border-t border-norlys-light-petroleum bg-white">
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
