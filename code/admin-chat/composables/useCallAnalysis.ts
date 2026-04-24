/**
 * useCallAnalysis — Composable for live conversation analysis via SSE.
 * Connects to the caller agent's analysis stream and provides
 * reactive analysis scores (15 categories, each 0-5).
 */

export interface AnalysisScores {
  PurchaseIntent: number;
  CustomerMood: number;
  Cooperativeness: number;
  BrandPerception: number;
  CompanySatisfaction: number;
  Urgency: number;
  Engagement: number;
  Frustration: number;
  TrustInAgent: number;
  ChurnRisk: number;
  UpsellOpportunity: number;
  ResolutionProgress: number;
  Politeness: number;
  CallEffectiveness: number;
  OverallSentiment: number;
  Timestamp: string;
}

export interface AnalysisCategory {
  key: keyof Omit<AnalysisScores, "Timestamp">;
  label: string;
  icon: string;
  color: string;
  /** Whether higher is "bad" (inverted for display) */
  inverted?: boolean;
}

export const ANALYSIS_CATEGORIES: AnalysisCategory[] = [
  {
    key: "OverallSentiment",
    label: "Overall Sentiment",
    icon: "🎯",
    color: "emerald",
  },
  {
    key: "CustomerMood",
    label: "Customer Mood",
    icon: "😊",
    color: "blue",
  },
  {
    key: "PurchaseIntent",
    label: "Purchase Intent",
    icon: "🛒",
    color: "violet",
  },
  {
    key: "Cooperativeness",
    label: "Cooperativeness",
    icon: "🤝",
    color: "teal",
  },
  {
    key: "Engagement",
    label: "Engagement",
    icon: "💬",
    color: "sky",
  },
  {
    key: "TrustInAgent",
    label: "Trust in Agent",
    icon: "🛡️",
    color: "indigo",
  },
  {
    key: "CompanySatisfaction",
    label: "Company Satisfaction",
    icon: "🏢",
    color: "cyan",
  },
  {
    key: "BrandPerception",
    label: "Brand Perception",
    icon: "⭐",
    color: "amber",
  },
  {
    key: "Politeness",
    label: "Politeness",
    icon: "🎩",
    color: "lime",
  },
  {
    key: "CallEffectiveness",
    label: "Call Effectiveness",
    icon: "📊",
    color: "green",
  },
  {
    key: "ResolutionProgress",
    label: "Resolution Progress",
    icon: "✅",
    color: "emerald",
  },
  {
    key: "UpsellOpportunity",
    label: "Upsell Opportunity",
    icon: "📈",
    color: "purple",
  },
  {
    key: "Urgency",
    label: "Urgency",
    icon: "⏰",
    color: "orange",
  },
  {
    key: "Frustration",
    label: "Frustration",
    icon: "😤",
    color: "red",
    inverted: true,
  },
  {
    key: "ChurnRisk",
    label: "Churn Risk",
    icon: "⚠️",
    color: "rose",
    inverted: true,
  },
];

export function useCallAnalysis() {
  const scores = ref<AnalysisScores | null>(null);
  const isConnected = ref(false);
  const callId = ref<string | null>(null);
  const callEnded = ref(false);
  const updateCount = ref(0);
  const { $appInsights } = useNuxtApp();

  let abortController: AbortController | null = null;

  async function connect(contextId: string) {
    // If already connected to same call, skip
    if (callId.value === contextId && isConnected.value) return;

    disconnect();
    callId.value = contextId;
    callEnded.value = false;
    scores.value = null;
    updateCount.value = 0;
    isConnected.value = true;

    abortController = new AbortController();

    try {
      console.log(`[Analysis] Connecting to SSE: /api/analysis/${contextId}`);
      const res = await fetch(`/api/analysis/${contextId}`, {
        headers: { Accept: "text/event-stream" },
        signal: abortController.signal,
      });

      if (!res.ok) {
        const errorBody = await res.text().catch(() => "");
        console.error(
          `[Analysis] SSE failed: ${res.status} ${res.statusText}`,
          errorBody,
        );
        $appInsights?.trackException({
          exception: new Error(`Analysis SSE ${res.status}: ${errorBody}`),
          properties: { contextId, component: "useCallAnalysis" },
        });
        isConnected.value = false;
        return;
      }

      console.log(`[Analysis] SSE connected, streaming...`);

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
            const evt = JSON.parse(data) as AnalysisScores;
            scores.value = evt;
            updateCount.value++;
            console.log(
              `[Analysis] Update #${updateCount.value}: sentiment=${evt.OverallSentiment}, mood=${evt.CustomerMood}`,
            );
          } catch (parseErr) {
            console.warn(`[Analysis] Malformed SSE data:`, data, parseErr);
          }
        }
      }

      // Stream ended normally
      console.log(`[Analysis] SSE stream ended normally`);
      callEnded.value = true;
      isConnected.value = false;
    } catch (e: any) {
      if (e.name !== "AbortError") {
        console.error(`[Analysis] SSE error:`, e);
        $appInsights?.trackException({
          exception: e instanceof Error ? e : new Error(String(e)),
          properties: { contextId, component: "useCallAnalysis" },
        });
      } else {
        console.log(`[Analysis] SSE aborted (client disconnect)`);
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
    scores.value = null;
    callId.value = null;
    callEnded.value = false;
    updateCount.value = 0;
  }

  return {
    scores: readonly(scores),
    isConnected: readonly(isConnected),
    callId: readonly(callId),
    callEnded: readonly(callEnded),
    updateCount: readonly(updateCount),
    connect,
    disconnect,
    clear,
  };
}
