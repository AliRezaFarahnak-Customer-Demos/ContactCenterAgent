using Azure;
using Azure.Communication.Email;
using Azure.Communication.Sms;
using CallAutomation.AzureAI.VoiceLive;   // CaseSummary
using Microsoft.ApplicationInsights;
using System.Text;
using System.Text.Json;

namespace CallerAgent.Outreach;

/// <summary>
/// Unified outreach orchestrator. One entry point (StartAsync) routes to the voice,
/// SMS or email channel; inbound replies are correlated back to the originating
/// outreach; results are persisted and optionally pushed to a caller-supplied
/// callback URL. Voice placement is injected (VoicePlacer) so the ACS call state
/// stays in Program.cs while this class stays the single router for REST + MCP.
/// </summary>
public sealed class OutreachService
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly OutreachStore _store;
    private readonly ILogger<OutreachService> _logger;
    private readonly TelemetryClient? _telemetry;
    private readonly SmsClient? _smsClient;
    private readonly EmailClient? _emailClient;
    private readonly string? _smsNumber;
    private readonly string? _emailSender;

    /// <summary>Set once at startup by Program.cs. Returns the voice-call contextId.</summary>
    public Func<OutreachRecord, Task<string?>>? VoicePlacer { get; set; }

    public OutreachService(OutreachStore store, IConfiguration config,
        ILogger<OutreachService> logger, TelemetryClient? telemetry = null)
    {
        _store = store;
        _logger = logger;
        _telemetry = telemetry;

        var acsConn = config["AcsConnectionString"];
        _smsNumber = config["AcsSmsNumber"];
        _emailSender = config["Email:SenderAddress"];

        if (!string.IsNullOrWhiteSpace(acsConn))
        {
            _smsClient = new SmsClient(acsConn);
            _emailClient = new EmailClient(acsConn);
        }
    }

    public bool SmsEnabled => _smsClient is not null && !string.IsNullOrWhiteSpace(_smsNumber);
    public bool EmailEnabled => _emailClient is not null && !string.IsNullOrWhiteSpace(_emailSender);

    // -----------------------------------------------------------------------
    // Start
    // -----------------------------------------------------------------------
    public async Task<OutreachResult> StartAsync(OutreachRequest req)
    {
        var channel = ResolveChannel(req);
        var customerId = FirstNonEmpty(req.Customer.Id, req.Customer.Phone, req.Customer.Email) ?? Guid.NewGuid().ToString("N");

        var record = new OutreachRecord
        {
            CustomerId = customerId,
            CustomerName = req.Customer.Name,
            Phone = req.Customer.Phone,
            Email = req.Customer.Email,
            Channel = channel,
            Intent = req.Intent,
            Message = req.Message,
            Context = req.Context,
            CallbackUrl = req.CallbackUrl,
            Status = "pending"
        };

        var messageText = req.Message ?? DefaultMessageForIntent(req.Intent);

        try
        {
            switch (channel)
            {
                case "voice":
                    if (VoicePlacer is null)
                    {
                        record.Status = "failed";
                        record.Reply = "Voice channel not available on this host.";
                        break;
                    }
                    record.Interactions.Add(new Interaction { Channel = "voice", Direction = "outbound", Text = "Udgående opkald påbegyndt." });
                    record.ContextId = await VoicePlacer(record);
                    record.Status = "pending";
                    break;

                case "sms":
                    if (!SmsEnabled || string.IsNullOrWhiteSpace(req.Customer.Phone))
                        throw new InvalidOperationException("SMS channel not available or no phone number provided.");
                    await _smsClient!.SendAsync(from: _smsNumber, to: req.Customer.Phone, message: messageText);
                    record.Interactions.Add(new Interaction { Channel = "sms", Direction = "outbound", Text = messageText });
                    record.Status = "awaiting_reply";
                    break;

                case "email":
                    if (!EmailEnabled || string.IsNullOrWhiteSpace(req.Customer.Email))
                        throw new InvalidOperationException("Email channel not available or no email address provided.");
                    var subject = req.Context is not null && req.Context.TryGetValue("subject", out var s) ? s : "Norlys – vi vil gerne i kontakt";
                    await _emailClient!.SendAsync(WaitUntil.Started, new EmailMessage(
                        senderAddress: _emailSender,
                        content: new EmailContent(subject) { PlainText = messageText },
                        recipients: new EmailRecipients(new[] { new EmailAddress(req.Customer.Email) })));
                    record.Interactions.Add(new Interaction { Channel = "email", Direction = "outbound", Text = $"{subject}\n\n{messageText}" });
                    record.Status = "awaiting_reply";
                    break;

                default:
                    record.Status = "failed";
                    record.Reply = $"Unknown channel '{channel}'.";
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Outreach start failed for channel {Channel}", channel);
            record.Status = "failed";
            record.Reply = ex.Message;
        }

        await _store.UpsertAsync(record);
        _telemetry?.TrackEvent("OutreachStarted", new Dictionary<string, string>
        {
            { "outreach.channel", channel },
            { "outreach.status", record.Status },
            { "outreach.id", record.Id }
        });
        return record.ToResult();
    }

    // -----------------------------------------------------------------------
    // Inbound reply correlation
    // -----------------------------------------------------------------------
    public Task HandleInboundSmsAsync(string from, string message) => HandleInboundAsync("sms", from, message);
    public Task HandleInboundEmailAsync(string from, string subject, string body) =>
        HandleInboundAsync("email", from, string.IsNullOrWhiteSpace(subject) ? body : $"{subject}\n\n{body}");

    private async Task HandleInboundAsync(string channel, string from, string text)
    {
        var record = await _store.FindLatestAwaitingReplyAsync(from);
        if (record is null)
        {
            // Unmatched inbound — still capture it so nothing is lost in the timeline.
            record = new OutreachRecord
            {
                CustomerId = from,
                Phone = channel == "sms" ? from : null,
                Email = channel == "email" ? from : null,
                Channel = channel,
                Status = "completed"
            };
        }

        record.Interactions.Add(new Interaction { Channel = channel, Direction = "inbound", Text = text });
        record.Reply = text;
        record.Status = "completed";
        await _store.UpsertAsync(record);

        _telemetry?.TrackEvent("OutreachInboundReply", new Dictionary<string, string>
        {
            { "outreach.channel", channel }, { "outreach.id", record.Id }
        });
        await FireCallbackAsync(record);
    }

    // -----------------------------------------------------------------------
    // Voice finalization (called on CallDisconnected, then enriched with summary)
    // -----------------------------------------------------------------------
    public async Task FinalizeVoiceAsync(string contextId, CaseSummary? summary)
    {
        var record = await _store.FindByContextIdAsync(contextId);
        if (record is null) return;

        if (summary is not null)
        {
            record.Summary = summary.Summary;
            record.Outcome = summary.Outcome;
            record.Topics = summary.Topics.ToList();
            record.Reply = summary.Summary;
            record.Collected = new Dictionary<string, string> { ["verified"] = summary.Verified.ToString().ToLowerInvariant() };
            record.Metrics ??= new Dictionary<string, string>();
            record.Metrics["follow_up_needed"] = summary.FollowUpNeeded.ToString().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(summary.FollowUpDraft))
                record.Metrics["follow_up_draft"] = summary.FollowUpDraft;
            record.Interactions.Add(new Interaction { Channel = "voice", Direction = "inbound", Text = summary.Summary });
        }

        record.Status = summary?.Outcome switch
        {
            "no_answer" => "no_answer",
            _ => "completed"
        };
        await _store.UpsertAsync(record);
        if (summary is not null) await FireCallbackAsync(record);
    }

    // -----------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------
    public async Task<OutreachResult?> GetResultAsync(string id) => (await _store.FindByIdAsync(id))?.ToResult();

    public IReadOnlyList<ChannelCapability> ListChannels() => new[]
    {
        new ChannelCapability("voice", true, true, true, null, "Global reach incl. Danish numbers."),
        new ChannelCapability("sms", SmsEnabled, SmsEnabled, SmsEnabled, _smsNumber,
            SmsEnabled ? "US/Canada two-way. Danish numbers require a DK sender." : "Not configured."),
        new ChannelCapability("email", EmailEnabled, EmailEnabled, false, _emailSender,
            EmailEnabled ? "Outbound global. Inbound reply capture requires a custom domain." : "Not configured.")
    };

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private string ResolveChannel(OutreachRequest req)
    {
        var c = (req.Channel ?? "auto").Trim().ToLowerInvariant();
        if (c is "voice" or "sms" or "email") return c;
        // auto
        if (!string.IsNullOrWhiteSpace(req.Customer.Phone)) return "voice";
        if (!string.IsNullOrWhiteSpace(req.Customer.Email)) return "email";
        return "sms";
    }

    private static string DefaultMessageForIntent(string? intent) => intent switch
    {
        "verify_address" => "Hej, det er Norlys. Vi mangler at bekræfte din adresse. Kan du bekræfte din vejadresse og postnummer?",
        _ => "Hej, det er Norlys. Vi vil gerne i kontakt med dig angående din sag."
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private async Task FireCallbackAsync(OutreachRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.CallbackUrl)) return;
        try
        {
            var json = JsonSerializer.Serialize(record.ToResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await s_http.PostAsync(record.CallbackUrl, new StringContent(json, Encoding.UTF8, "application/json"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Callback POST failed for outreach {Id}", record.Id);
        }
    }
}
