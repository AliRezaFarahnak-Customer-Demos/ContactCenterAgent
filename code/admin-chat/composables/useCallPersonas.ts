/**
 * Personas composable — API-backed.
 *
 * The backend (`/api/personas`) is the single source of truth for the per-call
 * system prompt and all composer defaults. The UI loads them on mount and
 * pushes edits back with a 1-second debounce (see CallComposer.vue).
 *
 * The persona's `prompt` field uses placeholders that the backend substitutes
 * when placing a call: {{customerName}}, {{phoneNumber}}, {{personaLabel}},
 * {{verificationFacts}}, {{notes}}, {{language}}.
 */

export interface CallPersona {
  id: string;
  label: string;
  emoji: string;
  description: string;
  prompt: string;
  customerName: string;
  countryCode: string;
  phoneNumber: string;
  verificationFacts: string;
  notes: string;
  language: string;
  languageCode: string;
}

/** Reactive shared state — populated by `loadPersonas()`. */
export function usePersonas() {
  return useState<CallPersona[]>("personas", () => []);
}

/** Fetch personas from the backend and populate shared state. */
export async function loadPersonas(): Promise<CallPersona[]> {
  const personas = await $fetch<CallPersona[]>("/api/personas");
  usePersonas().value = personas;
  return personas;
}

/** Get a persona by id from the currently-loaded shared state. */
export function getPersona(id: string): CallPersona | undefined {
  return usePersonas().value.find((p) => p.id === id);
}

/** Persist a partial update for a single persona. */
export async function savePersona(
  id: string,
  patch: Partial<Omit<CallPersona, "id">>,
): Promise<void> {
  const res = await $fetch<{ success: boolean; persona?: CallPersona }>(
    `/api/personas/${id}`,
    {
      method: "PUT",
      body: patch,
    },
  );
  if (res?.success && res.persona) {
    const all = usePersonas().value;
    const idx = all.findIndex((p) => p.id === id);
    if (idx !== -1) all[idx] = res.persona;
  }
}
