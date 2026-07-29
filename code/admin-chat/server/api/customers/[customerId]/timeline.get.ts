/** GET /api/customers/:customerId/timeline — proxy a customer's cross-channel timeline. */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const base = (config.callerAgentUrl as string).replace(/\/$/, "");
  const customerId = getRouterParam(event, "customerId");
  try {
    return await $fetch(
      `${base}/api/customers/${encodeURIComponent(customerId ?? "")}/timeline`,
    );
  } catch {
    return [];
  }
});
