/**
 * useCallLog — Composable for live call log events via SSE.
 * Connects to the caller agent's call log stream and provides
 * a reactive list of inbound/outbound call events.
 */

export interface CallLogEntry {
  direction: string; // "inbound" | "outbound" | "ended"
  phoneNumber: string;
  status: string; // "ringing" | "connected" | "disconnected"
  contextId: string;
  name: string | null;
  purpose: string | null;
  timestamp: string;
}

export function useCallLog() {
  const entries = ref<CallLogEntry[]>([]);
  const isConnected = ref(false);
  const unreadCount = ref(0);
  const { $appInsights } = useNuxtApp();

  let abortController: AbortController | null = null;
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  async function connect() {
    if (isConnected.value) return;

    disconnect();
    // Don't clear entries on reconnect — backend replays history and dedup handles it
    isConnected.value = true;

    abortController = new AbortController();

    try {
      console.log(`[CallLog] Connecting to SSE: /api/calls/stream`);
      const res = await fetch(`/api/calls/stream`, {
        headers: { Accept: "text/event-stream" },
        signal: abortController.signal,
      });

      if (!res.ok) {
        const errorBody = await res.text().catch(() => "");
        console.error(
          `[CallLog] SSE failed: ${res.status} ${res.statusText}`,
          errorBody,
        );
        $appInsights?.trackException({
          exception: new Error(`CallLog SSE ${res.status}: ${errorBody}`),
          properties: { component: "useCallLog" },
        });
        isConnected.value = false;
        return;
      }

      console.log(`[CallLog] SSE connected, streaming...`);

      const reader = res.body!.getReader();
      const decoder = new TextDecoder();
      let buffer = "";

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n");
        buffer = lines.pop() ?? "";

        for (const line of lines) {
          if (!line.startsWith("data: ")) continue;
          const data = line.slice(6).trim();

          if (data === "[DONE]") {
            isConnected.value = false;
            return;
          }

          try {
            const evt = JSON.parse(data) as {
              Direction: string;
              PhoneNumber: string;
              Status: string;
              ContextId: string;
              Name: string | null;
              Purpose: string | null;
              Timestamp: string;
            };

            console.log(
              `[CallLog] ${evt.Direction} call ${evt.Status}: ${evt.PhoneNumber}`,
            );

            // Deduplicate: check if we already have this exact event
            const isDuplicate = entries.value.some(
              (e) =>
                e.contextId === evt.ContextId &&
                e.direction === evt.Direction &&
                e.status === evt.Status,
            );

            if (!isDuplicate) {
              entries.value.push({
                direction: evt.Direction,
                phoneNumber: evt.PhoneNumber,
                status: evt.Status,
                contextId: evt.ContextId,
                name: evt.Name,
                purpose: evt.Purpose,
                timestamp: evt.Timestamp,
              });
              unreadCount.value++;
            }
          } catch (parseErr) {
            console.warn(`[CallLog] Malformed SSE data:`, data, parseErr);
            $appInsights?.trackException({
              exception:
                parseErr instanceof Error
                  ? parseErr
                  : new Error(String(parseErr)),
              properties: {
                component: "useCallLog",
                rawData: data.slice(0, 200),
              },
            });
          }
        }
      }

      console.log(`[CallLog] SSE stream ended normally`);
      isConnected.value = false;
      scheduleReconnect();
    } catch (e: any) {
      if (e.name !== "AbortError") {
        console.error(`[CallLog] SSE error:`, e);
        $appInsights?.trackException({
          exception: e instanceof Error ? e : new Error(String(e)),
          properties: { component: "useCallLog" },
        });
        scheduleReconnect();
      } else {
        console.log(`[CallLog] SSE aborted (client disconnect)`);
      }
      isConnected.value = false;
    }
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    console.log(`[CallLog] Will reconnect in 3s...`);
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      connect();
    }, 3000);
  }

  function disconnect() {
    if (reconnectTimer) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    abortController?.abort();
    abortController = null;
    isConnected.value = false;
  }

  function clearUnread() {
    unreadCount.value = 0;
  }

  function clear() {
    disconnect();
    entries.value = [];
    unreadCount.value = 0;
  }

  return {
    entries: readonly(entries),
    isConnected: readonly(isConnected),
    unreadCount: readonly(unreadCount),
    connect,
    disconnect,
    clearUnread,
    clear,
  };
}
