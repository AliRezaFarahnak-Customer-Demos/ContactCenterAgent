/**
 * Nuxt server route: proxies live call log SSE from the caller agent.
 * GET /api/calls/stream → Caller Agent SSE endpoint
 * Streams SSE events back to the browser with per-chunk flushing.
 */
export default defineEventHandler(async (event) => {
  const config = useRuntimeConfig();
  const callerAgentUrl = config.callerAgentUrl as string;

  const targetUrl = `${callerAgentUrl.replace(/\/$/, "")}/api/calls/stream`;

  console.log(`[SSE Proxy] Call log stream: fetching upstream: ${targetUrl}`);

  // Create an AbortController linked to the client request so we cancel
  // the upstream fetch when the browser disconnects.
  const abortController = new AbortController();
  event.node.req.on("close", () => {
    console.log(`[SSE Proxy] Call log client disconnected, aborting upstream`);
    abortController.abort();
  });

  let upstream: Response;
  try {
    upstream = await fetch(targetUrl, {
      headers: { Accept: "text/event-stream" },
      signal: abortController.signal,
    });
  } catch (fetchErr: any) {
    console.error(
      `[SSE Proxy] Call log upstream fetch failed:`,
      fetchErr.message,
    );
    throw createError({
      statusCode: 502,
      statusMessage: `Cannot reach caller agent: ${fetchErr.message}`,
    });
  }

  if (!upstream.ok) {
    const body = await upstream.text().catch(() => "");
    console.error(
      `[SSE Proxy] Call log upstream error: ${upstream.status} ${upstream.statusText}`,
      body,
    );
    throw createError({
      statusCode: upstream.status,
      statusMessage: `Caller agent error: ${upstream.statusText} — ${body}`,
    });
  }

  console.log(`[SSE Proxy] Call log upstream connected, streaming to client`);

  // Stream SSE back to the browser
  const res = event.node.res;
  res.writeHead(200, {
    "Content-Type": "text/event-stream",
    "Cache-Control": "no-cache",
    Connection: "keep-alive",
    "X-Accel-Buffering": "no",
  });
  res.flushHeaders();

  if (upstream.body) {
    const reader = (upstream.body as ReadableStream<Uint8Array>).getReader();
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        res.write(value);
      }
    } catch {
      // Client disconnected or upstream aborted
    } finally {
      reader.releaseLock();
    }
  }

  res.end();
});
