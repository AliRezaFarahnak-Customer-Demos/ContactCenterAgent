/** GET /api/channels — proxy the outreach channel capability matrix. */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  try {
    return await $fetch(`${base}/api/channels`);
  } catch {
    return [];
  }
});
