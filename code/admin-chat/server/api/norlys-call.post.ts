/**
 * POST /api/norlys-call
 * Builds a Norlys persona-aware system prompt with MFA verification rules,
 * then proxies an outbound call request to the caller agent.
 *
 * Request body:
 *   {
 *     persona: "onboarding" | "billing" | string,
 *     personaLabel?: string,
 *     personaInstructions: string,   // base persona system prompt (no MFA, no facts)
 *     customerName: string,
 *     phoneNumber: string,           // local number, no country code, may include spaces
 *     countryCode: string,           // e.g. "45" — no leading +
 *     verificationFacts: string,     // free text the agent must verify against
 *     notes?: string,                // optional extra context
 *     language?: string,             // e.g. "Danish"
 *     languageCode?: string          // ISO 639-1, e.g. "da"
 *   }
 *
 * Response: { success, contextId?, phoneNumber?, error? }
 */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const callerAgentUrl = (config.callerAgentUrl as string).replace(/\/$/, "");

  const body = await readBody<{
    persona: string;
    personaLabel?: string;
    personaInstructions: string;
    customerName: string;
    phoneNumber: string;
    countryCode: string;
    verificationFacts: string;
    notes?: string;
    language?: string;
    languageCode?: string;
  }>(event);

  if (!body?.phoneNumber || !body?.customerName || !body?.personaInstructions) {
    setResponseStatus(event, 400);
    return {
      success: false,
      error: "phoneNumber, customerName, and personaInstructions are required",
    };
  }

  // Normalize phone number to E.164
  const cc = (body.countryCode || "45").replace(/[^\d]/g, "");
  const local = body.phoneNumber.replace(/[^\d]/g, "").replace(/^0+/, "");
  const fullNumber = `+${cc}${local}`;

  const language = body.language || "Danish";
  const languageCode = body.languageCode || "da";
  const personaLabel = body.personaLabel || body.persona;

  // Build the per-call system prompt. The caller-agent appends its own
  // CorePhoneRules on top of this string.
  const facts = body.verificationFacts?.trim() || "(none provided)";
  const notes = body.notes?.trim();

  const systemPrompt = [
    `You are an AI customer service representative.`,
    `You are calling ${body.customerName} at ${fullNumber}.`,
    ``,
    `## Your role: ${personaLabel}`,
    body.personaInstructions.trim(),
    ``,
    `## MANDATORY identity verification (MFA) — DO THIS FIRST`,
    `Before discussing ANY account details, billing information, technical setup, or anything sensitive, you MUST verify the caller's identity.`,
    `OPEN the call by briefly explaining why: "For at beskytte din konto skal jeg lige stille et par hurtige sikkerhedsspørgsmål, før vi går videre."`,
    `Then ask 1–2 light security questions, one at a time, in a friendly tone (e.g. "Kan du bekræfte din adresse?" or "Hvilken e-mail har vi registreret på dig?").`,
    `If the answer matches the recorded facts, confirm and proceed. If it does NOT match after two attempts, politely explain you cannot continue and end the call.`,
    `Do NOT reveal the verification answers yourself, and do NOT proceed to the main topic until verification passes.`,
    ``,
    `### Verification facts on file (CONFIDENTIAL — never read aloud):`,
    facts,
    ``,
    notes ? `## Additional context for this call:\n${notes}\n` : "",
    `## Style`,
    `- Speak naturally and warmly in ${language}.`,
    `- Keep turns short and conversational.`,
    `- If asked who you are, say you're "den digitale assistent".`,
    `- If the customer wants a human, offer to transfer or arrange a callback.`,
  ]
    .filter(Boolean)
    .join("\n");

  try {
    const upstream = await fetch(`${callerAgentUrl}/api/outboundCall`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        phoneNumber: fullNumber,
        purpose: personaLabel,
        systemPrompt,
        name: body.customerName,
        language,
        languageCode,
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
