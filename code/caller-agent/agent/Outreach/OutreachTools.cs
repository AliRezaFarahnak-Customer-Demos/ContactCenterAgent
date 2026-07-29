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

    [McpServerTool(Name = "list_channels")]
    [Description("List the available outreach channels and their current capabilities (enabled, inbound/outbound, reach constraints).")]
    public static IReadOnlyList<ChannelCapability> ListChannels(OutreachService svc) => svc.ListChannels();
}
