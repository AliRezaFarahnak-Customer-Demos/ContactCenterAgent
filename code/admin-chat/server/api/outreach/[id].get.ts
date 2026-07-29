/** GET /api/outreach/:id — proxy the structured outreach result from the caller-agent. */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  const id = getRouterParam(event, "id");
  try {
    return await $fetch(`${base}/api/outreach/${id}`);
  } catch (err: unknown) {
    setResponseStatus(event, 404);
    return { error: err instanceof Error ? err.message : String(err) };
  }
});
