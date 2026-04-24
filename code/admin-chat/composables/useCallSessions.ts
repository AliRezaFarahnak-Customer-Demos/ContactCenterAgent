/**
 * useCallSessions — manages multiple concurrent live call sessions.
 *
 * Each session has its own transcription + analysis SSE streams. Sessions are
 * created when a call is initiated (or detected from inbound call log events),
 * and remain in the list (in "ended" state) until the user clears them.
 *
 * Singleton across the app via `useState`.
 */
import type { AnalysisScores } from "~/composables/useCallAnalysis";
import type { TranscriptEntry } from "~/composables/useCallTranscription";

export interface CallSession {
  contextId: string;
  customerName: string;
  phoneNumber: string;
  personaLabel: string;
  direction: "outbound" | "inbound";
  startedAt: string;
  status: "ringing" | "live" | "ended";
  // Live data (not reactive across instances — Vue refs are managed inside)
  transcript: TranscriptEntry[];
  analysis: AnalysisScores | null;
  analysisUpdates: number;
  // Internal: for cleanup
  _abort?: AbortController;
  _analysisAbort?: AbortController;
  // UI state
  expanded: boolean;
}

export function useCallSessions() {
  const sessions = useState<CallSession[]>("call-sessions", () => []);

  function findById(contextId: string): CallSession | undefined {
    return sessions.value.find((s) => s.contextId === contextId);
  }

  function addSession(input: {
    contextId: string;
    customerName: string;
    phoneNumber: string;
    personaLabel: string;
    direction: "outbound" | "inbound";
  }): CallSession {
    const existing = findById(input.contextId);
    if (existing) return existing;

    const session = reactive<CallSession>({
      contextId: input.contextId,
      customerName: input.customerName,
      phoneNumber: input.phoneNumber,
      personaLabel: input.personaLabel,
      direction: input.direction,
      startedAt: new Date().toISOString(),
      status: "ringing",
      transcript: [],
      analysis: null,
      analysisUpdates: 0,
      expanded: true,
    }) as CallSession;

    sessions.value.push(session);
    connectStreams(session);
    return session;
  }

  function connectStreams(session: CallSession) {
    connectTranscription(session);
    connectAnalysis(session);
  }

  async function connectTranscription(session: CallSession) {
    const ac = new AbortController();
    session._abort = ac;

    try {
      const res = await fetch(`/api/transcription/${session.contextId}`, {
        headers: { Accept: "text/event-stream" },
        signal: ac.signal,
      });
      if (!res.ok || !res.body) {
        console.error(`[Sessions] Transcription failed: ${res.status}`);
        return;
      }
      session.status = "live";
      await readSse(res.body, ac.signal, (data) => {
        if (data === "[DONE]") {
          session.status = "ended";
          return;
        }
        try {
          const evt = JSON.parse(data) as {
            Speaker: string;
            Text: string;
            Timestamp: string;
          };
          const speaker = evt.Speaker as TranscriptEntry["speaker"];
          // Merge consecutive entries from same speaker within 4s
          const last = session.transcript[session.transcript.length - 1];
          const timeDiffMs = last
            ? new Date(evt.Timestamp).getTime() -
              new Date(last.timestamp).getTime()
            : Infinity;
          if (last && last.speaker === speaker && timeDiffMs < 4000) {
            last.text += " " + evt.Text;
            last.timestamp = evt.Timestamp;
          } else {
            session.transcript.push({
              speaker,
              text: evt.Text,
              timestamp: evt.Timestamp,
            });
          }
        } catch {
          /* ignore */
        }
      });
    } catch (err: unknown) {
      if ((err as Error).name !== "AbortError") {
        console.error("[Sessions] Transcription error:", err);
      }
    } finally {
      // If stream ended naturally without an explicit callEnded event, mark ended
      if (session.status !== "ended") session.status = "ended";
    }
  }

  async function connectAnalysis(session: CallSession) {
    const ac = new AbortController();
    session._analysisAbort = ac;

    try {
      const res = await fetch(`/api/analysis/${session.contextId}`, {
        headers: { Accept: "text/event-stream" },
        signal: ac.signal,
      });
      if (!res.ok || !res.body) return;

      await readSse(res.body, ac.signal, (data) => {
        if (data === "[DONE]") return;
        try {
          const score = JSON.parse(data) as AnalysisScores;
          session.analysis = score;
          session.analysisUpdates += 1;
        } catch {
          /* ignore */
        }
      });
    } catch (err: unknown) {
      if ((err as Error).name !== "AbortError") {
        console.error("[Sessions] Analysis error:", err);
      }
    }
  }

  function removeSession(contextId: string) {
    const idx = sessions.value.findIndex((s) => s.contextId === contextId);
    if (idx === -1) return;
    const s = sessions.value[idx];
    s._abort?.abort();
    s._analysisAbort?.abort();
    sessions.value.splice(idx, 1);
  }

  function clearEnded() {
    for (const s of sessions.value.filter((x) => x.status === "ended")) {
      s._abort?.abort();
      s._analysisAbort?.abort();
    }
    sessions.value = sessions.value.filter((s) => s.status !== "ended");
  }

  function toggleExpanded(contextId: string) {
    const s = findById(contextId);
    if (s) s.expanded = !s.expanded;
  }

  return {
    sessions,
    addSession,
    findById,
    removeSession,
    clearEnded,
    toggleExpanded,
  };
}

// ---------------------------------------------------------------------------
// SSE line reader — splits chunks into "data: ..." events
// ---------------------------------------------------------------------------
async function readSse(
  body: ReadableStream<Uint8Array>,
  signal: AbortSignal,
  onData: (data: string) => void,
) {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (!signal.aborted) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    let idx: number;
    while ((idx = buffer.indexOf("\n\n")) !== -1) {
      const rawEvent = buffer.slice(0, idx);
      buffer = buffer.slice(idx + 2);
      // Each event may have multiple "data:" lines — concat them.
      const dataLines = rawEvent
        .split("\n")
        .filter((l) => l.startsWith("data:"))
        .map((l) => l.slice(5).trimStart());
      if (dataLines.length === 0) continue;
      onData(dataLines.join("\n"));
    }
  }
}
