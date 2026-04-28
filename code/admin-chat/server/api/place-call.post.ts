/**
 * POST /api/place-call
 * Builds a persona-aware system prompt with MFA verification rules,
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
    `## MANDATORY identity verification (MFA) — DO THIS FIRST, NO EXCEPTIONS`,
    `Before discussing ANY account details, billing, technical setup, or anything sensitive, you MUST verify the caller's identity.`,
    `OPENING (one short sentence): "Hej, det er ${personaLabel} fra Norlys — jeg ringer kort om din sag."  Then STOP and wait.`,
    `As soon as the caller says hi / "ja" / "okay" / "hvad drejer det sig om" / anything that signals engagement, your VERY NEXT line — also one short sentence — must be: "Først har jeg lige et par hurtige sikkerhedsspørgsmål."  Then ask ONE short verification question and WAIT.`,
    `Ask the questions ONE at a time. Keep each question as short as possible while still being polite and friendly — never robotic.`,
    `If the answer matches the recorded facts → confirm briefly ("Tak, det passer.") and ask the NEXT question, or proceed if all questions are passed.`,
    `If the answer does NOT match → give ONE more polite attempt. If still wrong, say a short polite goodbye and call hang_up.`,
    `HARD RULE: Until ALL security questions are answered correctly, you MUST NOT discuss the actual purpose of the call, account details, billing, or anything sensitive. If the caller tries to steer the conversation, politely say in ONE short sentence: "Det forstår jeg — men jeg skal lige bekræfte din identitet først." Then re-ask the pending security question.`,
    `Do NOT reveal the verification answers yourself. Do NOT hint at them. Do NOT confirm partial matches.`,
    ``,
    `### Verification facts on file (CONFIDENTIAL — never read aloud):`,
    facts,
    ``,
    notes
      ? `## Additional context for this call (only after verification passes):\n${notes}\n`
      : "",
    `## Style — STRICT`,
    `- Always polite, warm, and human in ${language}.`,
    `- Keep replies concise — usually one or two sentences. Expand only when the topic genuinely needs an explanation.`,
    `- Never chain questions. Never volunteer extra info that wasn't asked for.`,
    `- If asked who you are, say "den digitale assistent" — nothing more.`,
    `- If the customer wants a human, briefly offer to transfer or arrange a callback.`,
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
