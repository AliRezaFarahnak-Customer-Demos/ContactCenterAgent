/**
 * useCallTranscription — Composable for live call transcription via SSE.
 * Connects to the caller agent's transcription stream and provides
 * a reactive list of transcript entries.
 */

export interface TranscriptEntry {
  speaker: "user" | "ai" | "system";
  text: string;
  timestamp: string;
}

export function useCallTranscription() {
  const entries = ref<TranscriptEntry[]>([]);
  const isConnected = ref(false);
  const callId = ref<string | null>(null);
  const callEnded = ref(false);
  const { $appInsights } = useNuxtApp();

  let abortController: AbortController | null = null;

  async function connect(contextId: string) {
    // If already connected to same call, skip
    if (callId.value === contextId && isConnected.value) return;

    disconnect();
    callId.value = contextId;
    callEnded.value = false;
    entries.value = [];
    isConnected.value = true;

    abortController = new AbortController();

    try {
      console.log(
        `[Transcription] Connecting to SSE: /api/transcription/${contextId}`,
      );
      const res = await fetch(`/api/transcription/${contextId}`, {
        headers: { Accept: "text/event-stream" },
        signal: abortController.signal,
      });

      if (!res.ok) {
        const errorBody = await res.text().catch(() => "");
        console.error(
          `[Transcription] SSE failed: ${res.status} ${res.statusText}`,
          errorBody,
        );
        $appInsights?.trackException({
          exception: new Error(`Transcription SSE ${res.status}: ${errorBody}`),
          properties: { contextId, component: "useCallTranscription" },
        });
        isConnected.value = false;
        return;
      }

      console.log(`[Transcription] SSE connected, streaming...`);

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
            callEnded.value = true;
            isConnected.value = false;
            return;
          }

          try {
            const evt = JSON.parse(data) as {
              Speaker: string;
              Text: string;
              Timestamp: string;
            };
            const speaker = evt.Speaker as TranscriptEntry["speaker"];
            console.log(`[Transcription] ${evt.Speaker}: ${evt.Text}`);

            // Merge consecutive entries from the same speaker within 4s
            // (VAD may split a single utterance into fragments)
            const last = entries.value[entries.value.length - 1];
            const timeDiffMs = last
              ? new Date(evt.Timestamp).getTime() -
                new Date(last.timestamp).getTime()
              : Infinity;

            if (last && last.speaker === speaker && timeDiffMs < 4000) {
              last.text += " " + evt.Text;
              last.timestamp = evt.Timestamp;
            } else {
              entries.value.push({
                speaker,
                text: evt.Text,
                timestamp: evt.Timestamp,
              });
            }
          } catch (parseErr) {
            console.warn(`[Transcription] Malformed SSE data:`, data, parseErr);
            $appInsights?.trackException({
              exception:
                parseErr instanceof Error
                  ? parseErr
                  : new Error(String(parseErr)),
              properties: {
                contextId,
                component: "useCallTranscription",
                rawData: data.slice(0, 200),
              },
            });
          }
        }
      }

      // Stream ended normally
      console.log(`[Transcription] SSE stream ended normally`);
      callEnded.value = true;
      isConnected.value = false;
    } catch (e: any) {
      if (e.name !== "AbortError") {
        console.error(`[Transcription] SSE error:`, e);
        $appInsights?.trackException({
          exception: e instanceof Error ? e : new Error(String(e)),
          properties: { contextId, component: "useCallTranscription" },
        });
      } else {
        console.log(`[Transcription] SSE aborted (client disconnect)`);
      }
      isConnected.value = false;
    }
  }

  function disconnect() {
    abortController?.abort();
    abortController = null;
    isConnected.value = false;
  }

  function clear() {
    disconnect();
    entries.value = [];
    callId.value = null;
    callEnded.value = false;
  }

  return {
    entries: readonly(entries),
    isConnected: readonly(isConnected),
    callId: readonly(callId),
    callEnded: readonly(callEnded),
    connect,
    disconnect,
    clear,
  };
}
