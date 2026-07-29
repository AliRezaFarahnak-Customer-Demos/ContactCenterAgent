/**
 * POST /api/outreach — proxy to the caller-agent unified outreach orchestrator.
 * Body: { customer:{id?,name?,phone?,email?}, channel, intent?, message?, context?, ... }
 */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  const body = await readBody(event);
  try {
    return await $fetch(`${base}/api/outreach`, { method: "POST", body });
  } catch (err: unknown) {
    setResponseStatus(event, 502);
    return { error: err instanceof Error ? err.message : String(err) };
  }
});
