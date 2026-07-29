/**
 * POST /api/outreach/simulate-reply — proxy a simulated customer reply to the caller-agent.
 * Demo aid: injects an inbound reply into a thread without a live carrier inbound (SMS/email),
 * so the dashboard shows a two-way conversation. Body: { from, message, channel? }.
 */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  const body = await readBody(event);
  try {
    return await $fetch(`${base}/api/outreach/simulate-reply`, {
      method: "POST",
      body,
    });
  } catch (err: unknown) {
    setResponseStatus(event, 502);
    return { error: err instanceof Error ? err.message : String(err) };
  }
});
