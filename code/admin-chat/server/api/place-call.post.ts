/**
 * POST /api/place-call
 *
 * Resolves the persona's editable prompt (loaded from the personas store),
 * substitutes runtime placeholders, and forwards the call to the caller-agent.
 *
 * The forwarded `systemPrompt` is sent to Voice Live VERBATIM — the C# caller
 * agent no longer appends any rules. All wording lives in one place: the
 * persona's `prompt` field, editable from the UI.
 *
 * Request body (sent by CallComposer):
 *   {
 *     personaId: string,
 *     customerName: string,
 *     phoneNumber: string,           // local, may include spaces
 *     countryCode: string,           // e.g. "45", no leading +
 *     verificationFacts?: string,
 *     notes?: string,
 *   }
 *
 * Placeholders supported in persona.prompt:
 *   {{customerName}}, {{phoneNumber}}, {{personaLabel}},
 *   {{verificationFacts}}, {{notes}}, {{language}}
 */
import { getPersona } from "../utils/personasStore";

export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const callerAgentUrl = (config.callerAgentUrl as string).replace(/\/$/, "");

  const body = await readBody<{
    personaId: string;
    customerName: string;
    phoneNumber: string;
    countryCode: string;
    verificationFacts?: string;
    notes?: string;
  }>(event);

  if (!body?.personaId || !body?.customerName || !body?.phoneNumber) {
    setResponseStatus(event, 400);
    return {
      success: false,
      error: "personaId, customerName, and phoneNumber are required",
    };
  }

  const persona = await getPersona(body.personaId);
  if (!persona) {
    setResponseStatus(event, 404);
    return { success: false, error: `persona '${body.personaId}' not found` };
  }

  // Normalize phone number to E.164
  const cc = (body.countryCode || persona.countryCode || "45").replace(
    /[^\d]/g,
    "",
  );
  const local = body.phoneNumber.replace(/[^\d]/g, "").replace(/^0+/, "");
  const fullNumber = `+${cc}${local}`;

  // Substitute placeholders into the persona's editable prompt.
  // SECURITY: never inject the actual verification values. The model leaks them
  // regardless of prompt warnings. We send only the field names so the model
  // knows WHAT to ask for, not what the answer is.
  const facts = (body.verificationFacts ?? persona.verificationFacts).trim();
  const notes = (body.notes ?? persona.notes).trim();

  const factFieldNames = facts
    ? facts
        .split("\n")
        .map((line) => line.split(":")[0].trim())
        .filter(Boolean)
        .join(", ")
    : "";
  const redactedFacts = factFieldNames
    ? `Du KENDER IKKE kundens faktiske oplysninger \u2014 de er bevidst skjult for dig af sikkerhedsgrunde.\nFelter du kan bede kunden oplyse: ${factFieldNames}.\nN\u00e5r kunden svarer, sig blot \"tak\" og forts\u00e6t \u2014 en menneskelig medarbejder verificerer bagefter. Sig ALDRIG en adresse, e-mail eller andet du ikke har f\u00e5et direkte af kunden i denne samtale.`
    : "(ingen verifikationskrav)";

  const systemPrompt = persona.prompt
    .replaceAll("{{customerName}}", body.customerName)
    .replaceAll("{{phoneNumber}}", fullNumber)
    .replaceAll("{{personaLabel}}", persona.label)
    .replaceAll("{{verificationFacts}}", redactedFacts)
    .replaceAll("{{notes}}", notes || "(ingen)")
    .replaceAll("{{language}}", persona.language);

  try {
    const upstream = await fetch(`${callerAgentUrl}/api/outboundCall`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        phoneNumber: fullNumber,
        purpose: persona.label,
        systemPrompt,
        name: body.customerName,
        language: persona.language,
        languageCode: persona.languageCode,
        transcriptionHint: undefined,
      }),
    });

    const responseText = await upstream.text();

    if (!upstream.ok) {
      setResponseStatus(event, upstream.status);
      return {
        success: false,
        error: `Caller agent returned ${upstream.status}: ${responseText}`,
      };
    }

    let contextId: string | undefined;
    try {
      const json = JSON.parse(responseText);
      contextId = json.contextId ?? json.ContextId;
    } catch {
      /* ignore */
    }

    return {
      success: true,
      contextId,
      phoneNumber: fullNumber,
    };
  } catch (err: unknown) {
    setResponseStatus(event, 502);
    return {
      success: false,
      error: err instanceof Error ? err.message : String(err),
    };
  }
});
