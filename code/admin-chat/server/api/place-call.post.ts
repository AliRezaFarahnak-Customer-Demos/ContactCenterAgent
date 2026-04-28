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
  // The result is sent to Voice Live as-is — no additional rules appended.
  const facts = (body.verificationFacts ?? persona.verificationFacts).trim();
  const notes = (body.notes ?? persona.notes).trim();

  const systemPrompt = persona.prompt
    .replaceAll("{{customerName}}", body.customerName)
    .replaceAll("{{phoneNumber}}", fullNumber)
    .replaceAll("{{personaLabel}}", persona.label)
    .replaceAll("{{verificationFacts}}", facts || "(ingen oplyst)")
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
