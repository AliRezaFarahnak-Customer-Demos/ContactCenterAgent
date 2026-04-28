/**
 * Personas — bundled JSON.
 *
 * Loaded via ES import so Nitro inlines it into the server bundle. Works in
 * dev (npm run dev) and in production Docker without any runtime file lookup.
 *
 * The canonical file at the repo root (../../../../personas.json) is the
 * source of truth for the danish-voice-lab console app. We ship a copy at
 * `server/personas.json` for the web app — keep them in sync (or replace
 * with a build step that copies one to the other).
 */
import data from "../personas.json";

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

export async function loadPersonas(): Promise<CallPersona[]> {
  return data as CallPersona[];
}
