/**
 * Personas composable. Loads in-memory defaults from /api/personas. No save-back.
 */
export interface CallPersona {
  id: string;
  label: string;
  emoji: string;
  description: string;
  prompt: string;
  countryCode: string;
  phoneNumber: string;
  language: string;
  languageCode: string;
}

export function usePersonas() {
  return useState<CallPersona[]>("personas", () => []);
}

export async function loadPersonas(): Promise<CallPersona[]> {
  const personas = await $fetch<CallPersona[]>("/api/personas");
  usePersonas().value = personas;
  return personas;
}
