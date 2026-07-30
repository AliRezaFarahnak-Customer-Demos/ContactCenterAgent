/** POST /api/outreach/:id/followup — send a real follow-up message on an existing outreach thread. */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  const id = getRouterParam(event, "id");
  const body = await readBody(event);
  try {
    return await $fetch(`${base}/api/outreach/${id}/followup`, {
      method: "POST",
      body,
    });
  } catch (err: unknown) {
    setResponseStatus(event, 502);
    return { error: err instanceof Error ? err.message : String(err) };
  }
});
