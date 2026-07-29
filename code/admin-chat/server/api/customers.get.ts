/** GET /api/customers — proxy the cross-channel customer list. */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  try {
    return await $fetch(`${base}/api/customers`);
  } catch {
    return [];
  }
});
