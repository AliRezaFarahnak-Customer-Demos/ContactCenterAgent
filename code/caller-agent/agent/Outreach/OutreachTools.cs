using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CallerAgent.Outreach;

/// <summary>
/// MCP tools mirroring the REST outreach API, so any MCP client (incl. local VS Code)
/// can drive multi-channel outreach. Thin wrappers over OutreachService — one source
/// of truth with the REST handlers. Hosted anonymously at /mcp.
/// </summary>
[McpServerToolType]
public sealed class OutreachTools
{
    [McpServerTool(Name = "start_outreach")]
    [Description("Start a customer outreach on a channel. Returns an outreachId immediately; poll get_outreach_result, or supply callbackUrl to be pushed the structured result when the customer responds. Channels: voice, sms, email, or auto — call list_channels for the current reach and inbound/outbound capability of each.")]
    public static async Task<OutreachResult> StartOutreach(
        OutreachService svc,
        [Description("Channel: voice, sms, email, or auto")] string channel,
        [Description("Customer display name")] string? name = null,
        [Description("Customer phone in E.164, e.g. +4512345678")] string? phone = null,
        [Description("Customer email address")] string? email = null,
        [Description("Named intent playbook, e.g. verify_address")] string? intent = null,
        [Description("Literal message body (SMS/email) or voice system prompt")] string? message = null,
        [Description("Optional stable customer id (defaults to phone/email)")] string? customerId = null,
        [Description("Optional URL to POST the structured result to when complete")] string? callbackUrl = null,
        [Description("Optional key/value context for the conversation, e.g. caseId, accountNumber, meterNumber")] Dictionary<string, string>? context = null,
        [Description("Optional persona id from personas.json (voice channel)")] string? persona = null,
        [Description("Optional BCP-47 locale, e.g. da-DK")] string? locale = null)
    {
        var req = new OutreachRequest(
            new OutreachCustomer(customerId, name, phone, email),
            channel, intent, message, Context: context, CallbackUrl: callbackUrl,
            Persona: persona, Locale: locale);
        return await svc.StartAsync(req);
    }

    [McpServerTool(Name = "get_outreach_result")]
    [Description("Get the current status and structured result of an outreach by its outreachId: the customer reply, collected fields, full cross-channel interaction timeline, case summary/outcome/topics, and metrics including sentiment (satisfaction, problem_solved, overall_sentiment, frustration, churn_risk) scored 0-6 where 3 is neutral.")]
    public static async Task<OutreachResult?> GetOutreachResult(
        OutreachService svc,
        [Description("The outreachId returned by start_outreach")] string outreachId)
        => await svc.GetResultAsync(outreachId);

    [McpServerTool(Name = "wait_for_outreach_result")]
    [Description("Block until an outreach finishes (completed, failed or no_answer) or the timeout elapses, then return the same structured result as get_outreach_result. Use this instead of a polling loop when you need the voice call or the customer's reply before continuing. Returns the latest known state if the timeout is hit.")]
    public static async Task<OutreachResult?> WaitForOutreachResult(
        OutreachService svc,
        [Description("The outreachId returned by start_outreach")] string outreachId,
        [Description("Seconds to wait before returning the latest state anyway (5-600, default 120)")] int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
        => await svc.WaitForResultAsync(outreachId, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "send_followup")]
    [Description("Send a follow-up message on an EXISTING outreach thread — e.g. a written recap after a voice call, or a reply to something the customer wrote. Keeps the whole conversation on one record so the cross-channel timeline stays intact. Defaults to the outreach's own channel; a voice outreach falls back to SMS, or email if SMS can't reach the customer.")]
    public static async Task<OutreachResult?> SendFollowUp(
        OutreachService svc,
        [Description("The outreachId to continue")] string outreachId,
        [Description("Message body to send")] string message,
        [Description("Optional channel override: sms or email")] string? channel = null,
        [Description("Optional email subject")] string? subject = null)
        => await svc.SendFollowUpAsync(outreachId, message, channel, subject);

    [McpServerTool(Name = "list_outreach")]
    [Description("List recent outreaches across all customers and channels, newest first, with status, summary, outcome and metrics. Use this to see what has already been attempted before starting something new.")]
    public static async Task<IReadOnlyList<OutreachResult>> ListOutreach(
        OutreachService svc,
        [Description("Maximum number of outreaches to return (1-200, default 50)")] int limit = 50)
        => await svc.ListRecentAsync(limit);

    [McpServerTool(Name = "list_customers")]
    [Description("List known customers rolled up across all their outreaches: which channels have been used, how many outreaches, last activity and last outcome. Returns the customerId to pass to get_customer_timeline.")]
    public static async Task<IReadOnlyList<CustomerSummary>> ListCustomers(OutreachService svc)
        => await svc.ListCustomersAsync();

    [McpServerTool(Name = "get_customer_timeline")]
    [Description("Get a customer's complete cross-channel history — every voice call, SMS and email in order, with each outreach's summary, outcome and metrics. Use this for context before contacting them again.")]
    public static async Task<IReadOnlyList<OutreachResult>> GetCustomerTimeline(
        OutreachService svc,
        [Description("Customer id from list_customers (phone or email when no explicit id was set)")] string customerId)
        => await svc.GetCustomerTimelineAsync(customerId);

    [McpServerTool(Name = "list_channels")]
    [Description("List the available outreach channels and their current capabilities (enabled, inbound/outbound, reach constraints). Check this before choosing a channel — SMS and email reach differ by country and configuration.")]
    public static IReadOnlyList<ChannelCapability> ListChannels(OutreachService svc) => svc.ListChannels();

    [McpServerTool(Name = "list_personas")]
    [Description("List the voice personas defined in personas.json. Pass one of these ids as the 'persona' argument to start_outreach to control how the AI agent behaves on a voice call.")]
    public static IReadOnlyList<PersonaSummary> ListPersonas(OutreachService svc) => svc.ListPersonas();
}
