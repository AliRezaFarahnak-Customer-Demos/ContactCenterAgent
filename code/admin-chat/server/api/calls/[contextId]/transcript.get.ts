/** GET /api/calls/:contextId/transcript — proxy the pollable transcript + summary from the caller-agent. */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  const contextId = encodeURIComponent(getRouterParam(event, "contextId") ?? "");
  try {
    return await $fetch(`${base}/api/calls/${contextId}/transcript`);
  } catch (err: unknown) {
    setResponseStatus(event, 404);
    return { error: err instanceof Error ? err.message : String(err) };
  }
});
