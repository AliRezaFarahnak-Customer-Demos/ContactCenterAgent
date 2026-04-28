/**
 * Persona store — single source of truth for the per-persona system prompt
 * and all composer defaults. Persisted via Nitro storage (`useStorage('data')`),
 * which writes to `.data/kv/` on disk inside the container/dev machine.
 *
 * The frontend loads personas via GET /api/personas and PUTs edits with a
 * 1-second debounce. The C# caller-agent receives the resolved prompt verbatim
 * (no more CorePhoneRules / MFA blocks layered on top).
 *
 * The `prompt` field supports these placeholders, substituted in
 * place-call.post.ts at call time:
 *   {{customerName}}, {{phoneNumber}}, {{personaLabel}},
 *   {{verificationFacts}}, {{notes}}, {{language}}
 */

export interface CallPersona {
  id: string;
  label: string;
  emoji: string;
  description: string;
  /** Single compact system prompt with {{placeholders}}. Editable from the UI. */
  prompt: string;
  /** Default composer values. All editable from the UI and persisted per persona. */
  customerName: string;
  countryCode: string;
  phoneNumber: string;
  verificationFacts: string;
  notes: string;
  language: string;
  languageCode: string;
}

const STORAGE_KEY = "personas.json";

const DEFAULT_PERSONAS: CallPersona[] = [
  {
    id: "onboarding",
    label: "Onboarding & modem-installation",
    emoji: "📡",
    description:
      "Hjælper en ny kunde gennem opsætning af router og fiberforbindelse.",
    prompt: `Du er {{personaLabel}} hos Norlys og ringer til {{customerName}} på {{phoneNumber}}.

## Formål
Hjælp en ny kunde i gang med deres internetforbindelse og router.
1. Bekræft at routeren og fiberboksen er pakket ud og strømmen er tilsluttet.
2. Guid trin-for-trin: fiberkabel i WAN-porten, tænd routeren, vent ~2 min på konstant grøn lampe, forbind enheden til Wi-Fi (navn og kode står på undersiden).
3. Spørg om alt virker. Lav en hurtig hastighedstest hvis muligt.
4. Tilbyd at booke en tekniker hvis noget ikke virker.
Hvis kunden allerede er online: ros dem og afslut høfligt.

## Identitetskontrol (gør dette FØRST)
Åbning: "Hej, det er {{personaLabel}} fra Norlys — jeg ringer kort om din sag." Vent på svar.
Når kunden svarer: "Først har jeg lige et par hurtige sikkerhedsspørgsmål." Stil ÉT spørgsmål ad gangen baseret på fakta nedenfor og vent.
Hvis svaret matcher → bekræft kort og fortsæt. Hvis ikke → ét høfligt forsøg mere, ellers afslut høfligt og kald hang_up.
Diskutér ikke selve formålet før alle spørgsmål er besvaret korrekt.

### Verifikationsfakta (FORTROLIGT — læs aldrig højt)
{{verificationFacts}}

## Ekstra kontekst
{{notes}}

## Stil
- Tal {{language}} hele samtalen igennem.
- Hold svarene naturlige og samtalebaserede — typisk 1–3 sætninger. Udvid kun hvis emnet kræver en forklaring.
- Stil ét spørgsmål ad gangen og lyt færdigt.
- Sig aldrig at du er en AI. Sig "den digitale assistent" hvis du bliver spurgt.
- Afslut først samtalen når kunden eksplicit siger farvel — så svarer du kort og kalder hang_up.`,
    customerName: "Mette Hansen",
    countryCode: "45",
    phoneNumber: "",
    verificationFacts: `Adresse: Hovedgaden 12, 8000 Aarhus C
Email: kunde@example.dk`,
    notes:
      "Kunden har bestilt Fiber 1000/1000 og fået leveret en router i denne uge.",
    language: "Danish",
    languageCode: "da",
  },
  {
    id: "billing",
    label: "Forklaring af regning",
    emoji: "🧾",
    description:
      "Ringer en kunde op for at gennemgå seneste faktura og besvare spørgsmål.",
    prompt: `Du er {{personaLabel}} hos Norlys og ringer til {{customerName}} på {{phoneNumber}}.

## Formål
Gennemgå kundens seneste regning, fordi de har bedt om en forklaring.
1. Forklar at du ringer ift. den seneste faktura.
2. Gennemgå hovedposterne ud fra konteksten: abonnement / fast pris, forbrug (el/gas/internet), eventuelle gebyrer eller engangsbeløb.
3. Svar på spørgsmål. Hvis kunden er uenig, tilbyd at oprette en sag til regningsteamet.
4. Mind kunden om at detaljer altid kan ses på selvbetjeningen.
Vær empatisk hvis kunden er overrasket over beløbet.

## Identitetskontrol (gør dette FØRST)
Åbning: "Hej, det er {{personaLabel}} fra Norlys — jeg ringer kort om din sag." Vent på svar.
Når kunden svarer: "Først har jeg lige et par hurtige sikkerhedsspørgsmål." Stil ÉT spørgsmål ad gangen baseret på fakta nedenfor og vent.
Hvis svaret matcher → bekræft kort og fortsæt. Hvis ikke → ét høfligt forsøg mere, ellers afslut høfligt og kald hang_up.
Diskutér ikke regningens detaljer før alle spørgsmål er besvaret korrekt.

### Verifikationsfakta (FORTROLIGT — læs aldrig højt)
{{verificationFacts}}

## Ekstra kontekst
{{notes}}

## Stil
- Tal {{language}} hele samtalen igennem.
- Hold svarene naturlige og samtalebaserede — typisk 1–3 sætninger. Udvid kun hvis emnet kræver en forklaring.
- Stil ét spørgsmål ad gangen og lyt færdigt.
- Sig aldrig at du er en AI. Sig "den digitale assistent" hvis du bliver spurgt.
- Afslut først samtalen når kunden eksplicit siger farvel — så svarer du kort og kalder hang_up.`,
    customerName: "Mette Hansen",
    countryCode: "45",
    phoneNumber: "",
    verificationFacts: `Adresse: Søndergade 4, 9000 Aalborg
Email: kunde@example.dk`,
    notes:
      "Seneste el-regning: 1.842 kr. (november 2025) — inkluderer 1.290 kr. forbrug, 450 kr. abonnement, 102 kr. afgifter.",
    language: "Danish",
    languageCode: "da",
  },
];

export async function loadPersonas(): Promise<CallPersona[]> {
  const storage = useStorage("data");
  const existing = await storage.getItem<CallPersona[]>(STORAGE_KEY);
  if (existing && Array.isArray(existing) && existing.length > 0) {
    // Merge in any new default personas added in code (by id) without overwriting edits
    const byId = new Map(existing.map((p) => [p.id, p]));
    let changed = false;
    for (const def of DEFAULT_PERSONAS) {
      if (!byId.has(def.id)) {
        existing.push(def);
        changed = true;
      }
    }
    if (changed) await storage.setItem(STORAGE_KEY, existing);
    return existing;
  }
  await storage.setItem(STORAGE_KEY, DEFAULT_PERSONAS);
  return DEFAULT_PERSONAS;
}

export async function getPersona(id: string): Promise<CallPersona | null> {
  const all = await loadPersonas();
  return all.find((p) => p.id === id) ?? null;
}

/**
 * Patch a persona. Only known fields are accepted; `id` is immutable.
 * Returns the updated persona, or null if id not found.
 */
export async function updatePersona(
  id: string,
  patch: Partial<Omit<CallPersona, "id">>,
): Promise<CallPersona | null> {
  const storage = useStorage("data");
  const all = await loadPersonas();
  const idx = all.findIndex((p) => p.id === id);
  if (idx === -1) return null;
  const allowed = [
    "label",
    "emoji",
    "description",
    "prompt",
    "customerName",
    "countryCode",
    "phoneNumber",
    "verificationFacts",
    "notes",
    "language",
    "languageCode",
  ] as const;
  const clean: Partial<CallPersona> = {};
  const src = patch as Record<string, unknown>;
  for (const key of allowed) {
    const value = src[key];
    if (typeof value === "string") {
      (clean as Record<string, string>)[key] = value;
    }
  }
  all[idx] = { ...all[idx], ...clean, id };
  await storage.setItem(STORAGE_KEY, all);
  return all[idx];
}
