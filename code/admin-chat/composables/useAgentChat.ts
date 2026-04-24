/**
 * useAgentChat — Composable for AG-UI protocol SSE streaming.
 * Connects directly to the .NET agent via a Nuxt server proxy.
 * No CopilotKit — pure AG-UI events over fetch + ReadableStream.
 */

export interface ChatMessage {
  id: string;
  role: "user" | "assistant" | "tool";
  content: string;
  toolCalls?: ToolCall[];
  toolCallId?: string;
  toolName?: string;
  complete: boolean;
}

export interface ToolCall {
  id: string;
  type: "function";
  function: {
    name: string;
    arguments: string;
  };
}

export function useAgentChat() {
  const messages = ref<ChatMessage[]>([]);
  const isLoading = ref(false);
  const threadId = ref(crypto.randomUUID());
  const error = ref<string | null>(null);
  const { $appInsights } = useNuxtApp();

  let abortController: AbortController | null = null;

  async function sendMessage(text: string) {
    if (!text.trim() || isLoading.value) return;

    error.value = null;

    // Finalize any incomplete messages from previous streaming sessions
    // so TOOL_CALL_START will create a new assistant message for this turn
    messages.value.forEach((m) => {
      m.complete = true;
    });

    // Add user message
    const userMsg: ChatMessage = {
      id: crypto.randomUUID(),
      role: "user",
      content: text.trim(),
      complete: true,
    };
    messages.value.push(userMsg);

    isLoading.value = true;
    abortController = new AbortController();

    // Build AG-UI request payload
    const payload = {
      threadId: threadId.value,
      runId: crypto.randomUUID(),
      state: {},
      messages: messages.value.map((m) => {
        if (m.role === "tool") {
          return {
            id: m.id,
            role: m.role,
            content: m.content,
            toolCallId: m.toolCallId,
          };
        }
        const msg: Record<string, unknown> = {
          id: m.id,
          role: m.role,
          content: m.content,
        };
        if (m.toolCalls?.length) {
          msg.toolCalls = m.toolCalls;
        }
        return msg;
      }),
      tools: [],
      context: [],
      forwardedProps: {},
    };

    try {
      const res = await fetch("/api/agent", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
        signal: abortController.signal,
      });

      if (!res.ok) {
        // Try to read error body for details (e.g. policy violation message)
        const errorBody = await res.text().catch(() => "");
        throw new Error(errorBody || `Agent returned ${res.status}`);
      }

      await parseSSEStream(res);

      // If the stream ended without producing any assistant content and
      // no RUN_ERROR was received, the request was likely blocked by a
      // content-safety policy. Surface a user-visible error.
      if (!error.value) {
        const lastAssistant = messages.value
          .filter((m) => m.role === "assistant")
          .at(-1);
        if (
          !lastAssistant ||
          (!lastAssistant.content && !lastAssistant.toolCalls?.length)
        ) {
          error.value =
            "No response received — the message may have been blocked by a content policy.";
        }
      }
    } catch (e: any) {
      if (e.name !== "AbortError") {
        error.value = e.message || "Connection failed";
        console.error("Agent error:", e);
        $appInsights?.trackException({
          exception: e instanceof Error ? e : new Error(String(e)),
          properties: { component: "useAgentChat", threadId: threadId.value },
        });
      }
    } finally {
      isLoading.value = false;
      abortController = null;
    }
  }

  async function parseSSEStream(res: Response) {
    const reader = res.body!.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    // Track tool calls for associating results
    const toolCallMap = new Map<string, { msgId: string; name: string }>();

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";

      for (const line of lines) {
        if (!line.startsWith("data: ")) continue;
        const data = line.slice(6).trim();
        if (!data || data === "[DONE]") continue;

        try {
          const event = JSON.parse(data);
          handleEvent(event, toolCallMap);
        } catch {
          // Skip malformed JSON
        }
      }
    }
  }

  function handleEvent(
    event: any,
    toolCallMap: Map<string, { msgId: string; name: string }>,
  ) {
    switch (event.type) {
      case "TEXT_MESSAGE_START": {
        const msg: ChatMessage = {
          id: event.messageId,
          role: "assistant",
          content: "",
          toolCalls: [],
          complete: false,
        };
        messages.value.push(msg);
        break;
      }

      case "TEXT_MESSAGE_CONTENT": {
        const msg = messages.value.find((m) => m.id === event.messageId);
        if (msg) msg.content += event.delta;
        break;
      }

      case "TEXT_MESSAGE_END": {
        const msg = messages.value.find((m) => m.id === event.messageId);
        if (msg) msg.complete = true;
        break;
      }

      case "TOOL_CALL_START": {
        const toolName = event.toolCallName ?? "unknown";

        // Always track tool name so TOOL_CALL_RESULT can look it up
        toolCallMap.set(event.toolCallId, { msgId: "", name: toolName });

        // Attach tool call to the current turn's assistant message.
        // Only reuse an *incomplete* assistant message (from this streaming session).
        // If the last assistant message is already complete (previous turn), create a new one
        // so tool_calls are grouped correctly in the conversation history.
        const assistantMsgs = messages.value.filter(
          (m) => m.role === "assistant",
        );
        let lastAssistant = assistantMsgs[assistantMsgs.length - 1];
        if (!lastAssistant || lastAssistant.complete) {
          lastAssistant = {
            id: crypto.randomUUID(),
            role: "assistant",
            content: "",
            toolCalls: [],
            complete: false,
          };
          messages.value.push(lastAssistant);
        }
        if (!lastAssistant.toolCalls) lastAssistant.toolCalls = [];
        lastAssistant.toolCalls.push({
          id: event.toolCallId,
          type: "function",
          function: {
            name: toolName,
            arguments: "",
          },
        });
        toolCallMap.set(event.toolCallId, {
          msgId: lastAssistant.id,
          name: toolName,
        });
        break;
      }

      case "TOOL_CALL_ARGS": {
        const assistantMsgs = messages.value.filter(
          (m) => m.role === "assistant",
        );
        const lastAssistant = assistantMsgs[assistantMsgs.length - 1];
        if (lastAssistant?.toolCalls) {
          const tc = lastAssistant.toolCalls.find(
            (t) => t.id === event.toolCallId,
          );
          if (tc) tc.function.arguments += event.delta;
        }
        break;
      }

      case "TOOL_CALL_END": {
        // Tool call specification complete; no UI action needed
        break;
      }

      case "TOOL_CALL_RESULT": {
        const info = toolCallMap.get(event.toolCallId);
        const toolMsg: ChatMessage = {
          id: event.messageId ?? crypto.randomUUID(),
          role: "tool",
          content: event.content ?? "",
          toolCallId: event.toolCallId,
          toolName: info?.name ?? "tool",
          complete: true,
        };
        messages.value.push(toolMsg);
        break;
      }

      case "RUN_ERROR": {
        error.value = event.message ?? "Agent encountered an error";
        $appInsights?.trackException({
          exception: new Error(event.message ?? "RUN_ERROR"),
          properties: {
            component: "useAgentChat",
            eventType: "RUN_ERROR",
            threadId: threadId.value,
          },
        });
        break;
      }

      // RUN_STARTED, RUN_FINISHED — lifecycle events, no UI action needed
    }
  }

  function stopGeneration() {
    abortController?.abort();
    isLoading.value = false;
  }

  function clearMessages() {
    messages.value = [];
    threadId.value = crypto.randomUUID();
    error.value = null;
  }

  return {
    messages: readonly(messages),
    isLoading: readonly(isLoading),
    threadId: readonly(threadId),
    error: readonly(error),
    sendMessage,
    stopGeneration,
    clearMessages,
  };
}
