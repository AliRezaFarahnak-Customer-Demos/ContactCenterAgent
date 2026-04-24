/**
 * Nuxt server route: proxies AG-UI requests to the .NET agent.
 * POST /api/agent → .NET agent SSE endpoint
 * Streams the SSE response back to the browser with per-chunk flushing
 * for character-level streaming (no buffering delay).
 */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const agentUrl = config.agentUrl;
  const body = await readBody(event);

  // Forward the request to the .NET agent
  const agentRes = await fetch(agentUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "text/event-stream",
    },
    body: JSON.stringify(body),
  });

  if (!agentRes.ok) {
    throw createError({
      statusCode: agentRes.status,
      statusMessage: `Agent error: ${agentRes.statusText}`,
    });
  }

  // Set SSE headers and flush immediately so the browser opens the stream
  const res = event.node.res;
  res.writeHead(200, {
    "Content-Type": "text/event-stream",
    "Cache-Control": "no-cache",
    Connection: "keep-alive",
    "X-Accel-Buffering": "no",
  });
  res.flushHeaders();

  // Read from the agent stream and flush every chunk individually.
  // This eliminates the Nitro sendStream buffering that batches
  // multiple SSE events into one TCP write.
  if (agentRes.body) {
    const reader = (agentRes.body as ReadableStream<Uint8Array>).getReader();
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        // Write and flush each chunk immediately — each chunk typically
        // contains one SSE "data:" line (one token delta)
        res.write(value);
        // Flush is available on ServerResponse when compression is off
        if (typeof (res as any).flush === "function") {
          (res as any).flush();
        }
      }
    } catch (err: any) {
      // If the client is still connected, write the error as an SSE event
      // so the frontend can surface it instead of silently dropping
      if (!res.writableEnded) {
        const errMsg = err?.message || "Upstream agent connection error";
        res.write(
          `data: ${JSON.stringify({ type: "RUN_ERROR", message: errMsg })}\n\n`,
        );
      }
    } finally {
      reader.releaseLock();
      res.end();
    }
  } else {
    res.end();
  }
});
