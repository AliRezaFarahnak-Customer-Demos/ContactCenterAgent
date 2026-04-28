/**
 * POST /api/place-call
 *
 * Demo flow: the UI owns the entire system prompt. We pass it through
 * verbatim. Only the phone number is normalized.
 *
 * Body:
 *   {
 *     personaLabel: string,   // shown in call log only
 *     phoneNumber: string,    // local digits, may include spaces
 *     countryCode: string,    // e.g. "45", no leading +
 *     prompt: string,         // full system prompt, sent verbatim to Voice Live
 *     language: string,       // e.g. "Danish"
 *     languageCode: string,   // e.g. "da"
 *   }
 */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const callerAgentUrl = (config.callerAgentUrl as string).replace(/\/$/, "");

  const body = await readBody<{
    personaLabel: string;
    phoneNumber: string;
    countryCode: string;
    prompt: string;
    language: string;
    languageCode: string;
  }>(event);

  if (!body?.phoneNumber || !body?.prompt || !body?.personaLabel) {
    setResponseStatus(event, 400);
    return {
      success: false,
      error: "personaLabel, phoneNumber, and prompt are required",
    };
  }

  const cc = (body.countryCode || "45").replace(/[^\d]/g, "");
  const local = body.phoneNumber.replace(/[^\d]/g, "").replace(/^0+/, "");
  const fullNumber = `+${cc}${local}`;

  try {
    const upstream = await fetch(`${callerAgentUrl}/api/outboundCall`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        phoneNumber: fullNumber,
        purpose: body.personaLabel,
        systemPrompt: body.prompt,
        name: body.personaLabel,
        language: body.language,
        languageCode: body.languageCode,
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
