/** DELETE /api/outreach — demo reset: wipe all customer sessions on the caller-agent. */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  try {
    return await $fetch(`${base}/api/outreach`, { method: "DELETE" });
  } catch (err: unknown) {
    setResponseStatus(event, 502);
    return { error: err instanceof Error ? err.message : String(err) };
  }
});
