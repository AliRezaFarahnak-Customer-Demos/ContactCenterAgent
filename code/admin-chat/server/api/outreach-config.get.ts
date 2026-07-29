/** GET /api/outreach-config — non-secret UI hints (MCP endpoint URL for VS Code). */
export default defineEventHandler(() => {
  const config = useRuntimeConfig();
  return {
    mcpUrl: (config.mcpUrl as string) || "",
  };
});
