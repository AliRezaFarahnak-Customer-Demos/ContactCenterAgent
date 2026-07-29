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
 *     voice?: string,         // da-DK voice name, omit to use the agent default
 *     voiceStyle?: string,    // HD Omni style; "" means explicitly no style
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
    voice?: string;
    voiceStyle?: string;
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

  // Substitute the literal "CustomerName" placeholder with the "Navn:" value
  // from the SECURITY QUESTIONS block at the top of the prompt. Keeps the
  // greeting personal ("Hej Mette, ...") without forcing the operator to
  // hand-edit every occurrence — they just edit `Navn:` once and it propagates.
  // If no Navn: is found, "CustomerName" stays as-is and the LLM will skip it
  // gracefully (Voice Live tends to read "Hej, jeg ringer …" naturally).
  let prompt = body.prompt;
  const navnMatch = prompt.match(/^[ \t]*Navn[ \t]*:[ \t]*(.+?)[ \t]*$/im);
  if (navnMatch) {
    const fullName = navnMatch[1].trim();
    // Use first name only for the greeting — feels more natural in Danish than the full name.
    const firstName = fullName.split(/\s+/)[0];
    prompt = prompt.replaceAll("CustomerName", firstName);
  }

  try {
    const upstream = await fetch(`${callerAgentUrl}/api/outboundCall`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        phoneNumber: fullNumber,
        purpose: body.personaLabel,
        systemPrompt: prompt,
        name: body.personaLabel,
        language: body.language,
        languageCode: body.languageCode,
        transcriptionHint: undefined,
        voice: body.voice || undefined,
        // "" is a real choice (no style), so only undefined falls back to the agent default.
        voiceStyle: body.voiceStyle,
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
