/**
 * Personas — loaded from the SHARED `personas.json` at the repo root.
 *
 * One file, two consumers: this Nuxt server util AND the danish-voice-lab
 * console app both read the same prompts. Edit `personas.json` at the repo
 * root → both apps pick it up next time they start.
 *
 * Path resolution: in dev, Nuxt runs from `code/admin-chat/`, so we walk up
 * two levels. The file is loaded once at module import.
 */
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

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

const SHARED_PATH = resolve(process.cwd(), "..", "..", "personas.json");

let cached: CallPersona[] | null = null;

function load(): CallPersona[] {
  if (cached) return cached;
  const raw = readFileSync(SHARED_PATH, "utf8");
  cached = JSON.parse(raw) as CallPersona[];
  return cached;
}

export async function loadPersonas(): Promise<CallPersona[]> {
  return load();
}
