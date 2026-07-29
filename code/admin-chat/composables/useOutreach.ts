// Cross-channel outreach client — talks to the caller-agent via the Nuxt proxy routes.

export interface ChannelCapability {
  channel: string;
  enabled: boolean;
  outbound: boolean;
  inbound: boolean;
  sender?: string | null;
  notes: string;
}

export interface OutreachInteraction {
  channel: string;
  direction: string; // outbound | inbound
  text: string;
  timestampUtc: string;
}

export interface OutreachResult {
  outreachId: string;
  status: string;
  channel: string;
  customerId: string;
  customerName?: string | null;
  reply?: string | null;
  summary?: string | null;
  outcome?: string | null;
  topics?: string[] | null;
  metrics?: Record<string, string> | null;
  interactions: OutreachInteraction[];
  createdUtc: string;
  updatedUtc: string;
}

export interface CustomerSummary {
  customerId: string;
  name?: string | null;
  phone?: string | null;
  email?: string | null;
  channels: string[];
  outreachCount: number;
  lastActivity: string;
  lastOutcome?: string | null;
}

export interface StartOutreachInput {
  channel: string;
  name?: string;
  phone?: string;
  email?: string;
  intent?: string;
  message?: string;
  voice?: string;
  voiceStyle?: string;
}

export function useOutreach() {
  const channels = ref<ChannelCapability[]>([]);
  const customers = ref<CustomerSummary[]>([]);
  const mcpUrl = ref<string>("");

  async function loadChannels() {
    try {
      channels.value = await $fetch<ChannelCapability[]>("/api/channels");
    } catch {
      channels.value = [];
    }
  }

  async function loadCustomers() {
    try {
      customers.value = await $fetch<CustomerSummary[]>("/api/customers");
    } catch {
      customers.value = [];
    }
  }

  async function loadTimeline(customerId: string): Promise<OutreachResult[]> {
    try {
      return await $fetch<OutreachResult[]>(
        `/api/customers/${encodeURIComponent(customerId)}/timeline`,
      );
    } catch {
      return [];
    }
  }

  async function loadMcpUrl() {
    try {
      const cfg = await $fetch<{ mcpUrl: string }>("/api/outreach-config");
      mcpUrl.value = cfg.mcpUrl || "";
    } catch {
      mcpUrl.value = "";
    }
  }

  async function startOutreach(
    input: StartOutreachInput,
  ): Promise<OutreachResult> {
    return await $fetch<OutreachResult>("/api/outreach", {
      method: "POST",
      body: {
        customer: {
          name: input.name || null,
          phone: input.phone || null,
          email: input.email || null,
        },
        channel: input.channel,
        intent: input.intent || null,
        message: input.message || null,
        voice: input.voice || null,
        voiceStyle: input.voiceStyle || null,
      },
    });
  }

  async function getResult(id: string): Promise<OutreachResult | null> {
    try {
      return await $fetch<OutreachResult>(`/api/outreach/${id}`);
    } catch {
      return null;
    }
  }

  // Demo aid: inject a customer reply so a two-way thread shows without a live carrier inbound.
  async function simulateReply(input: {
    from: string;
    message: string;
    channel?: string;
  }): Promise<boolean> {
    try {
      await $fetch("/api/outreach/simulate-reply", {
        method: "POST",
        body: input,
      });
      return true;
    } catch {
      return false;
    }
  }

  return {
    channels,
    customers,
    mcpUrl,
    loadChannels,
    loadCustomers,
    loadTimeline,
    loadMcpUrl,
    startOutreach,
    getResult,
    simulateReply,
  };
}
