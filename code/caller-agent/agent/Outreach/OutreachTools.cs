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
    // Annotations tell an MCP client which calls are safe to make freely and which actually
    // contact a customer, so it can require confirmation before placing a call or sending a message.
    [McpServerTool(Name = "start_outreach", Title = "Contact a customer",
        ReadOnly = false, Destructive = true, OpenWorld = true, UseStructuredContent = true)]
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
        [Description("Optional persona id from personas.json. Sets the agent's role and tone on every channel: the voice script, and the wording of drafted SMS and email.")] string? persona = null,
        [Description("Optional BCP-47 locale, e.g. da-DK")] string? locale = null,
        [Description("Stable idempotency key. Reusing it with the same request returns the original outreach without sending again.")] string? clientRequestId = null)
    {
        var req = new OutreachRequest(
            new OutreachCustomer(customerId, name, phone, email),
            channel, intent, message, Context: context, CallbackUrl: callbackUrl,
            Persona: persona, Locale: locale, ClientRequestId: clientRequestId);
        return await svc.StartAsync(req);
    }

    [McpServerTool(Name = "get_outreach_result", Title = "Get outreach result",
        ReadOnly = true, UseStructuredContent = true)]
    [Description("Get the current status and structured result of an outreach by its outreachId: the customer reply, collected fields, full cross-channel interaction timeline, case summary/outcome/topics, and metrics including sentiment (satisfaction, problem_solved, overall_sentiment, frustration, churn_risk) scored 0-6 where 3 is neutral.")]
    public static async Task<OutreachResult?> GetOutreachResult(
        OutreachService svc,
        [Description("The outreachId returned by start_outreach")] string outreachId)
        => await svc.GetResultAsync(outreachId);

    [McpServerTool(Name = "wait_for_outreach_result", Title = "Wait for outreach result",
        ReadOnly = true, UseStructuredContent = true)]
    [Description("Block until an outreach finishes (completed, failed or no_answer) or the timeout elapses, then return the same structured result as get_outreach_result. Use this instead of a polling loop when you need the voice call or the customer's reply before continuing. Returns the latest known state if the timeout is hit.")]
    public static async Task<OutreachResult?> WaitForOutreachResult(
        OutreachService svc,
        [Description("The outreachId returned by start_outreach")] string outreachId,
        [Description("Seconds to wait before returning the latest state anyway (5-600, default 120)")] int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
        => await svc.WaitForResultAsync(outreachId, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "send_followup", Title = "Send a follow-up message",
        ReadOnly = false, Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Send a follow-up message on an EXISTING outreach thread — e.g. a written recap after a voice call, or a reply to something the customer wrote. Keeps the whole conversation on one record so the cross-channel timeline stays intact. Defaults to the outreach's own channel; a voice outreach falls back to SMS, or email if SMS can't reach the customer.")]
    public static async Task<OutreachResult?> SendFollowUp(
        OutreachService svc,
        [Description("The outreachId to continue")] string outreachId,
        [Description("Message body to send")] string message,
        [Description("Optional channel override: sms or email")] string? channel = null,
        [Description("Optional email subject")] string? subject = null,
        [Description("Stable idempotency key. Reusing it returns the current thread without sending the follow-up again.")] string? clientRequestId = null)
        => await svc.SendFollowUpAsync(outreachId, message, channel, subject, clientRequestId);

    [McpServerTool(Name = "retry_callback", Title = "Retry result callback",
        ReadOnly = false, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Retry a failed or dead-lettered callback for a terminal outreach. Resets its attempt count, validates the target again, and returns the updated result including callback delivery state.")]
    public static async Task<OutreachResult?> RetryCallback(
        OutreachService svc,
        [Description("The outreachId whose callback should be retried")] string outreachId,
        CancellationToken cancellationToken = default)
        => await svc.RetryCallbackAsync(outreachId, cancellationToken);

    [McpServerTool(Name = "list_outreach", Title = "List recent outreaches",
        ReadOnly = true, UseStructuredContent = true)]
    [Description("List recent outreaches across all customers and channels, newest first, with status, summary, outcome and metrics. Use this to see what has already been attempted before starting something new.")]
    public static async Task<IReadOnlyList<OutreachResult>> ListOutreach(
        OutreachService svc,
        [Description("Maximum number of outreaches to return (1-200, default 50)")] int limit = 50)
        => await svc.ListRecentAsync(limit);

    [McpServerTool(Name = "list_customers", Title = "List customers",
        ReadOnly = true, UseStructuredContent = true)]
    [Description("List known customers rolled up across all their outreaches: which channels have been used, how many outreaches, last activity and last outcome. Returns the customerId to pass to get_customer_timeline.")]
    public static async Task<IReadOnlyList<CustomerSummary>> ListCustomers(OutreachService svc)
        => await svc.ListCustomersAsync();

    [McpServerTool(Name = "get_customer_timeline", Title = "Get customer timeline",
        ReadOnly = true, UseStructuredContent = true)]
    [Description("Get a customer's complete cross-channel history — every voice call, SMS and email in order, with each outreach's summary, outcome and metrics. Use this for context before contacting them again.")]
    public static async Task<IReadOnlyList<OutreachResult>> GetCustomerTimeline(
        OutreachService svc,
        [Description("Customer id from list_customers (phone or email when no explicit id was set)")] string customerId)
        => await svc.GetCustomerTimelineAsync(customerId);

    [McpServerTool(Name = "list_channels", Title = "List available channels",
        ReadOnly = true, UseStructuredContent = true)]
    [Description("List the available outreach channels and their current capabilities (enabled, inbound/outbound, reach constraints). Check this before choosing a channel — SMS and email reach differ by country and configuration.")]
    public static IReadOnlyList<ChannelCapability> ListChannels(OutreachService svc) => svc.ListChannels();

    [McpServerTool(Name = "list_personas", Title = "List personas",
        ReadOnly = true, UseStructuredContent = true)]
    [Description("List the personas defined in personas.json — the business scenarios the agent can run, such as onboarding or billing. Pass one id as the 'persona' argument to start_outreach to set the agent's role and tone. A persona applies to every channel: it drives the voice script and the wording of drafted SMS and email. Use list_channels to decide HOW to reach the customer, and list_personas to decide WHAT the agent should be doing.")]
    public static IReadOnlyList<PersonaSummary> ListPersonas(OutreachService svc) => svc.ListPersonas();
}
