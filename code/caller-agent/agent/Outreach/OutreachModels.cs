using Newtonsoft.Json;

namespace CallerAgent.Outreach;

// ---------------------------------------------------------------------------
// DTOs — the REST + MCP surface (System.Text.Json in ASP.NET / MCP SDK).
// ---------------------------------------------------------------------------

public record OutreachCustomer(string? Id, string? Name, string? Phone, string? Email);

/// <summary>Demo/testing aid: inject a customer reply into a thread without a live carrier inbound.</summary>
public record SimulateReplyRequest(string From, string Message, string? Channel = null, string? Subject = null);

/// <summary>Start-outreach request. One unified shape for voice, sms and email.</summary>
public record OutreachRequest(
    OutreachCustomer Customer,
    string Channel,                     // voice | sms | email | auto
    string? Intent = null,
    string? Message = null,             // literal message / voice system prompt
    Dictionary<string, string>? Context = null,
    string? CallbackUrl = null,         // optional push instead of poll
    string? Persona = null,
    string? Locale = null,
    string? Voice = null,
    string? VoiceStyle = null);

/// <summary>Structured result handed back to the calling agent.</summary>
public record OutreachResult(
    string OutreachId,
    string Status,                      // pending | awaiting_reply | completed | failed | no_answer
    string Channel,
    string CustomerId,
    string? CustomerName,
    string? Reply,
    Dictionary<string, string>? Collected,
    Dictionary<string, string>? Metrics,
    string? Summary,
    string? Outcome,
    string[]? Topics,
    IReadOnlyList<Interaction> Interactions,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>Capability descriptor returned by list_channels / GET /api/channels.</summary>
public record ChannelCapability(
    string Channel,
    bool Enabled,
    bool Outbound,
    bool Inbound,
    string? Sender,
    string Notes);

/// <summary>One customer rolled up across all their outreaches, for the roster view.</summary>
public record CustomerSummary(
    string CustomerId,
    string? Name,
    string? Phone,
    string? Email,
    string[] Channels,
    int OutreachCount,
    DateTimeOffset LastActivity,
    string? LastOutcome);

/// <summary>A selectable voice persona from personas.json (prompt body omitted — it's long).</summary>
public record PersonaSummary(string Id, string Label, string? Description, string? LanguageCode);

// ---------------------------------------------------------------------------
// Persisted entity — Cosmos (Newtonsoft serializer). One record per outreach;
// interactions are embedded (accessed together, well under the 2 MB item cap).
// Partitioned by /customerId so a customer's whole cross-channel history is a
// single-partition query.
// ---------------------------------------------------------------------------

public class Interaction
{
    [JsonProperty("channel")] public string Channel { get; set; } = "";
    [JsonProperty("direction")] public string Direction { get; set; } = ""; // outbound | inbound
    [JsonProperty("text")] public string Text { get; set; } = "";
    [JsonProperty("timestampUtc")] public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class OutreachRecord
{
    [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonProperty("customerId")] public string CustomerId { get; set; } = "";
    [JsonProperty("customerName")] public string? CustomerName { get; set; }
    [JsonProperty("phone")] public string? Phone { get; set; }
    [JsonProperty("email")] public string? Email { get; set; }
    [JsonProperty("channel")] public string Channel { get; set; } = "";
    [JsonProperty("intent")] public string? Intent { get; set; }
    [JsonProperty("message")] public string? Message { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = "pending";
    [JsonProperty("reply")] public string? Reply { get; set; }
    [JsonProperty("summary")] public string? Summary { get; set; }
    [JsonProperty("outcome")] public string? Outcome { get; set; }
    [JsonProperty("topics")] public List<string> Topics { get; set; } = new();
    [JsonProperty("collected")] public Dictionary<string, string>? Collected { get; set; }
    [JsonProperty("metrics")] public Dictionary<string, string>? Metrics { get; set; }
    [JsonProperty("context")] public Dictionary<string, string>? Context { get; set; }
    [JsonProperty("callbackUrl")] public string? CallbackUrl { get; set; }
    [JsonProperty("contextId")] public string? ContextId { get; set; }   // voice-call correlation id
    [JsonProperty("persona")] public string? Persona { get; set; }
    [JsonProperty("subject")] public string? Subject { get; set; }
    [JsonProperty("aiReplies")] public int AiReplies { get; set; }       // guards the auto-reply loop
    [JsonProperty("interactions")] public List<Interaction> Interactions { get; set; } = new();
    [JsonProperty("createdUtc")] public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [JsonProperty("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public OutreachResult ToResult() => new(
        OutreachId: Id,
        Status: Status,
        Channel: Channel,
        CustomerId: CustomerId,
        CustomerName: CustomerName,
        Reply: Reply,
        Collected: Collected,
        Metrics: Metrics,
        Summary: Summary,
        Outcome: Outcome,
        Topics: Topics.ToArray(),
        Interactions: Interactions,
        CreatedUtc: CreatedUtc,
        UpdatedUtc: UpdatedUtc);
}
