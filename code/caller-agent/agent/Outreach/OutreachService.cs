using Azure;
using Azure.AI.OpenAI;
using Azure.Communication.Email;
using Azure.Communication.Sms;
using Azure.Core;
using Azure.Identity;
using CallAutomation.AzureAI.VoiceLive;   // CaseSummary
using ContactCenterAgent.Shared.Personas;
using Microsoft.ApplicationInsights;
using OpenAI.Chat;
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
    private readonly string? _smsSenderId;
    private readonly string? _emailSender;
    // Azure Communication Services "Messaging Connect": SMS still goes out through the standard ACS
    // SmsClient, but ACS routes delivery to a partner carrier (Infobip) that leases/registers the
    // sender and handles local compliance. Observability, delivery reports and Event Grid stay in
    // Azure. This is the only SMS path enabled on this sandbox subscription (native alphanumeric and
    // number purchase are both blocked at the tenant level), and the Azure-native way to reach +45.
    private readonly string? _mcApiKey;
    private readonly string _mcPartner;
    private readonly string? _mcFrom;
    // Microsoft Graph sendMail — the Azure-native alternative to ACS Email. Sends from a real
    // mailbox (e.g. noreply@norlys.dk), so customer replies land in that mailbox and two-way
    // email works without attaching a custom domain to ACS. Auth is DefaultAzureCredential, so
    // it uses the container app's managed identity in Azure (no secret) and az login locally.
    // The identity needs the Graph application permission Mail.Send.
    private readonly string? _graphSender;
    private readonly bool _preferAcsDelivery;
    private readonly TokenCredential _credential = new DefaultAzureCredential();
    // Drafts the SMS/email body from intent + persona system prompt + context when the caller
    // supplies no literal message. Same Azure OpenAI resource the voice + analysis paths use.
    private readonly ChatClient? _draftClient;

    /// <summary>Set once at startup by Program.cs. Returns the voice-call contextId.</summary>
    public Func<OutreachRecord, Task<string?>>? VoicePlacer { get; set; }

    public OutreachService(OutreachStore store, IConfiguration config,
        ILogger<OutreachService> logger, TelemetryClient? telemetry = null)
    {
        _store = store;
        _logger = logger;
        _telemetry = telemetry;

        var acsConn = config["AcsConnectionString"];
        // Bicep materializes unset settings as EMPTY STRINGS, which are non-null and poison
        // ?? chains — normalize every optional config read to null.
        _smsNumber = NullIfEmpty(config["AcsSmsNumber"]);
        // Optional alphanumeric sender ID for branded outbound-only SMS outside US/CA.
        _smsSenderId = NullIfEmpty(config["Acs:SmsSenderId"]);
        _emailSender = NullIfEmpty(config["Email:SenderAddress"]);
        // Messaging Connect partner API key (Infobip, scope sms:message:send). The sender/number below
        // MUST be one Infobip has provisioned AND synced back to this ACS resource, otherwise ACS 401s.
        // Empty strings are treated as unset so a placeholder in appsettings.json stays inert.
        _mcApiKey = NullIfEmpty(config["MessagingConnect:ApiKey"]);
        var partner = config["MessagingConnect:Partner"];
        _mcPartner = string.IsNullOrWhiteSpace(partner) ? "infobip" : partner!;
        _mcFrom = NullIfEmpty(config["MessagingConnect:Sender"]) ?? _smsSenderId;

        var graphSender = config["Graph:SenderAddress"];
        _graphSender = string.IsNullOrWhiteSpace(graphSender) ? null : graphSender!.Trim();
        // Graph sendMail is fire-and-forget: it returns 202 even when the tenant can't deliver
        // externally (trial/sandbox tenants are blocked from outbound internet mail). ACS Email
        // reports a real delivery status, so it can be preferred for sending while the Graph
        // mailbox stays the Reply-To that GraphInboxPoller watches — delivery + two-way.
        _preferAcsDelivery = config.GetValue<bool?>("Email:PreferAcsDelivery") ?? false;

        var aoaiEndpoint = config["AzureOpenAI:Endpoint"];
        if (!string.IsNullOrWhiteSpace(aoaiEndpoint))
        {
            var deployment = config["AzureOpenAI:AnalysisDeploymentName"]
                ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.AnalysisModel;
            _draftClient = new AzureOpenAIClient(new Uri(aoaiEndpoint), _credential).GetChatClient(deployment);
        }

        if (!string.IsNullOrWhiteSpace(acsConn))
        {
            _smsClient = new SmsClient(acsConn);
            _emailClient = new EmailClient(acsConn);
        }

        // SMS can live on a DIFFERENT ACS resource than voice/email: number purchase and
        // alphanumeric senders require an eligible (PAYG/EA/CSP) subscription, so a sandbox
        // deployment keeps voice here and points Sms:ConnectionString at the eligible resource.
        var smsConn = config["Sms:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(smsConn))
        {
            _smsClient = new SmsClient(smsConn);
            _logger.LogInformation("SMS uses a dedicated ACS resource (Sms:ConnectionString).");
        }
    }

    // Messaging Connect is usable once we have a partner API key AND a synced sender/number to send from.
    private bool MessagingConnectEnabled =>
        _smsClient is not null && !string.IsNullOrWhiteSpace(_mcApiKey) && !string.IsNullOrWhiteSpace(_mcFrom);

    public bool SmsEnabled => MessagingConnectEnabled ||
        (_smsClient is not null && (!string.IsNullOrWhiteSpace(_smsNumber) || !string.IsNullOrWhiteSpace(_smsSenderId)));

    private bool GraphEmailEnabled => _graphSender is not null;

    public bool EmailEnabled => GraphEmailEnabled || (_emailClient is not null && !string.IsNullOrWhiteSpace(_emailSender));

    /// <summary>
    /// Send email via Microsoft Graph <c>sendMail</c> from a real mailbox. Unlike ACS Email on the
    /// Azure-managed domain, replies come back to that mailbox, so email is genuinely two-way.
    /// </summary>
    private async Task SendViaGraphAsync(string to, string subject, string body)
    {
        AccessToken token;
        try
        {
            token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Kunne ikke hente Graph-token: {ex.Message}");
        }

        var payload = new
        {
            message = new
            {
                subject,
                body = new { contentType = "Text", content = body },
                toRecipients = new[] { new { emailAddress = new { address = to } } }
            },
            saveToSentItems = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_graphSender!)}/sendMail");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.Token}");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await s_http.SendAsync(request);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Graph sendMail fejlede fra '{_graphSender}' (HTTP {(int)resp.StatusCode}): {detail}");
        }

        _logger.LogInformation("Graph sendMail accepted: to={To} from={From}", to, _graphSender);
    }

    /// <summary>
    /// Send an SMS through Azure Communication Services "Messaging Connect": the standard ACS SmsClient
    /// with a <see cref="MessagingConnectOptions"/> that carries the partner (Infobip) API key. ACS
    /// validates the request, then routes delivery to the partner while keeping observability, delivery
    /// reports and Event Grid inside Azure. This is the Azure-native way to reach Denmark (+45) from a
    /// subscription where native alphanumeric / DK numbers aren't available.
    /// </summary>
    private async Task SendViaMessagingConnectAsync(string to, string text)
    {
        var options = new SmsSendOptions(enableDeliveryReport: true)
        {
            MessagingConnect = new MessagingConnectOptions(_mcApiKey!, _mcPartner)
        };

        Azure.Response<SmsSendResult> resp;
        try
        {
            resp = await _smsClient!.SendAsync(from: _mcFrom!, to: to, message: text, options: options);
        }
        catch (Azure.RequestFailedException rfe)
        {
            // 401 = the 'from' sender isn't synced to this ACS resource yet (partner provisioning/
            // registration still pending); 400 = messagingConnect payload or partner mismatch.
            throw new InvalidOperationException(
                $"Messaging Connect afviste SMS fra '{_mcFrom}' (HTTP {rfe.Status}). " +
                "Kontroller at afsenderen er provisioneret hos partneren og synkroniseret til ACS-ressourcen.");
        }

        if (!resp.Value.Successful)
            throw new InvalidOperationException(
                $"Messaging Connect SMS afvist (HTTP {resp.Value.HttpStatusCode}): {resp.Value.ErrorMessage}");

        _logger.LogInformation("Messaging Connect SMS accepted: to={To} from={From} partner={Partner} messageId={MessageId}",
            to, _mcFrom, _mcPartner, resp.Value.MessageId);
    }

    // -----------------------------------------------------------------------
    // Start
    // -----------------------------------------------------------------------
    public async Task<OutreachResult> StartAsync(OutreachRequest req)
    {
        var channel = ResolveChannel(req);
        // Normalized so inbound replies (whose casing the carrier/Exchange controls) match.
        var phone = NormalizeAddress(req.Customer.Phone);
        var email = NormalizeAddress(req.Customer.Email);
        var customerId = FirstNonEmpty(req.Customer.Id, phone, email) ?? Guid.NewGuid().ToString("N");

        var record = new OutreachRecord
        {
            CustomerId = customerId,
            CustomerName = req.Customer.Name,
            Phone = phone,
            Email = email,
            Channel = channel,
            Intent = req.Intent,
            Message = req.Message,
            Context = req.Context,
            CallbackUrl = req.CallbackUrl,
            Status = "pending"
        };

        var messageText = req.Message;
        // No literal body: let the model write it from the intent, persona prompt and context.
        // Voice is excluded — there req.Message IS the system prompt, not a body to send.
        if (string.IsNullOrWhiteSpace(messageText) && channel is "sms" or "email")
            messageText = await DraftMessageAsync(channel, req);
        messageText ??= DefaultMessageForIntent(req.Intent);

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
                    record.Status = await DeliverAsync("sms", phone, null, messageText, "")
                        ? "awaiting_reply" : "completed";
                    record.Interactions.Add(new Interaction { Channel = "sms", Direction = "outbound", Text = messageText });
                    break;

                case "email":
                    var subject = req.Context is not null && req.Context.TryGetValue("subject", out var s) ? s : "Norlys – vi vil gerne i kontakt";
                    await DeliverAsync("email", null, email, messageText, subject);
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
            record.Reply = ex is Azure.RequestFailedException rfe
                ? $"{channel.ToUpperInvariant()} fejlede ({rfe.Status} {rfe.ErrorCode})."
                : ex.Message;
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
    // Delivery — shared by StartAsync and SendFollowUpAsync.
    // Returns true when a customer reply can route back to us on this channel.
    // -----------------------------------------------------------------------
    private async Task<bool> DeliverAsync(string channel, string? phone, string? email, string text, string subject)
    {
        switch (channel)
        {
            case "sms":
                if (!SmsEnabled || string.IsNullOrWhiteSpace(phone))
                    throw new InvalidOperationException("SMS-kanalen er ikke konfigureret, eller der mangler et telefonnummer.");
                var to = phone!.Trim();

                // Azure-native Messaging Connect reaches Denmark (+45) and 190+ countries — prefer it when configured.
                if (MessagingConnectEnabled)
                {
                    await SendViaMessagingConnectAsync(to, text);
                    return true;
                }

                // Sender routing: SMS senders are country-scoped, so the toll-free number only
                // reaches +1; everything else goes via the registered alphanumeric sender when
                // one is configured (branded, outbound-only). A name has no inbox, so replies
                // are only possible when sending from the number to +1.
                var isUsDestination = to.StartsWith("+1");
                string smsFrom;
                if (isUsDestination && !string.IsNullOrWhiteSpace(_smsNumber)) smsFrom = _smsNumber!;
                else if (!string.IsNullOrWhiteSpace(_smsSenderId)) smsFrom = _smsSenderId!;
                else if (!string.IsNullOrWhiteSpace(_smsNumber)) smsFrom = _smsNumber!;
                else throw new InvalidOperationException("Ingen SMS-afsender konfigureret.");

                Azure.Response<SmsSendResult> smsResp;
                try
                {
                    smsResp = await _smsClient!.SendAsync(from: smsFrom, to: to, message: text);
                }
                catch (Azure.RequestFailedException rfe)
                {
                    throw new InvalidOperationException(
                        $"SMS kunne ikke sendes fra '{smsFrom}' (HTTP {rfe.Status}). " +
                        (isUsDestination ? "" : "SMS til lande uden for US/Canada kræver en registreret alfanumerisk afsender (Acs:SmsSenderId), et lokalt nummer eller Messaging Connect."));
                }
                if (!smsResp.Value.Successful)
                    throw new InvalidOperationException($"SMS afvist af ACS (HTTP {smsResp.Value.HttpStatusCode}): {smsResp.Value.ErrorMessage}");
                return isUsDestination && smsFrom == _smsNumber;

            case "email":
                if (!EmailEnabled || string.IsNullOrWhiteSpace(email))
                    throw new InvalidOperationException("Email channel not available or no email address provided.");

                var useAcs = _emailClient is not null && !string.IsNullOrWhiteSpace(_emailSender)
                             && (_preferAcsDelivery || !GraphEmailEnabled);
                if (useAcs)
                {
                    var message = new EmailMessage(
                        senderAddress: _emailSender,
                        content: new EmailContent(subject) { PlainText = text },
                        recipients: new EmailRecipients(new[] { new EmailAddress(email) }));
                    // Replies land in the monitored mailbox, so the poller can still thread them.
                    if (_graphSender is not null) message.ReplyTo.Add(new EmailAddress(_graphSender));
                    await _emailClient!.SendAsync(WaitUntil.Started, message);
                    _logger.LogInformation("ACS Email accepted: to={To} from={From} replyTo={ReplyTo}",
                        email, _emailSender, _graphSender ?? "(none)");
                }
                else
                {
                    await SendViaGraphAsync(email!, subject, text);
                }
                return true;

            default:
                throw new InvalidOperationException($"Kanalen '{channel}' understøtter ikke direkte beskeder.");
        }
    }

    /// <summary>
    /// Send a follow-up on an existing outreach thread — e.g. the written recap of a voice call,
    /// or a reply to the customer's inbound message. Keeps everything on the same record so the
    /// cross-channel timeline stays whole.
    /// </summary>
    public async Task<OutreachResult?> SendFollowUpAsync(string outreachId, string message, string? channel = null, string? subject = null)
    {
        var record = await _store.FindByIdAsync(outreachId);
        if (record is null) return null;

        // Voice can't carry a written follow-up, so fall back to whichever text channel we can reach.
        var ch = (channel ?? (record.Channel == "voice"
            ? (SmsEnabled && !string.IsNullOrWhiteSpace(record.Phone) ? "sms" : "email")
            : record.Channel)).Trim().ToLowerInvariant();
        var subj = string.IsNullOrWhiteSpace(subject) ? "Norlys – opfølgning" : subject!;

        try
        {
            var canReply = await DeliverAsync(ch, record.Phone, record.Email, message, subj);
            record.Interactions.Add(new Interaction
            {
                Channel = ch,
                Direction = "outbound",
                Text = ch == "email" ? $"{subj}\n\n{message}" : message
            });
            record.Status = canReply ? "awaiting_reply" : "completed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Follow-up failed for outreach {Id} on {Channel}", outreachId, ch);
            record.Status = "failed";
            record.Reply = ex.Message;
        }

        await _store.UpsertAsync(record);
        _telemetry?.TrackEvent("OutreachFollowUp", new Dictionary<string, string>
        {
            { "outreach.channel", ch }, { "outreach.status", record.Status }, { "outreach.id", record.Id }
        });
        return record.ToResult();
    }

    // -----------------------------------------------------------------------
    // Inbound reply correlation
    // -----------------------------------------------------------------------
    public Task HandleInboundSmsAsync(string from, string message) => HandleInboundAsync("sms", from, message);
    public Task HandleInboundEmailAsync(string from, string subject, string body) =>
        HandleInboundAsync("email", from, string.IsNullOrWhiteSpace(subject) ? body : $"{subject}\n\n{body}");

    /// <summary>
    /// Thread an emailed reply onto an existing outreach. A mailbox also receives unrelated
    /// human mail, so unlike SMS to a dedicated number, mail from an address we never contacted
    /// is ignored instead of being captured as a new customer. Returns true when it was threaded.
    /// </summary>
    public async Task<bool> TryHandleInboundEmailAsync(string from, string subject, string body)
    {
        var record = await _store.FindLatestAwaitingReplyAsync(NormalizeAddress(from)!);
        if (record is null) return false;
        await AppendInboundAsync(record, "email", string.IsNullOrWhiteSpace(subject) ? body : $"{subject}\n\n{body}");
        return true;
    }

    private async Task HandleInboundAsync(string channel, string from, string text)
    {
        from = NormalizeAddress(from)!;
        var record = await _store.FindLatestAwaitingReplyAsync(from)
            // Unmatched inbound — still capture it so nothing is lost in the timeline.
            ?? new OutreachRecord
            {
                CustomerId = from,
                Phone = channel == "sms" ? from : null,
                Email = channel == "email" ? from : null,
                Channel = channel,
                Status = "completed"
            };

        await AppendInboundAsync(record, channel, text);
    }

    private async Task AppendInboundAsync(OutreachRecord record, string channel, string text)
    {
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
            // Sentiment scores are scored 0-6 where 3 = neutral.
            if (summary.Analysis is { } a)
            {
                record.Metrics["sentiment_scale"] = "0-6 (3 = neutral)";
                record.Metrics["satisfaction"] = a.CompanySatisfaction.ToString();
                record.Metrics["problem_solved"] = a.ResolutionProgress.ToString();
                record.Metrics["overall_sentiment"] = a.OverallSentiment.ToString();
                record.Metrics["customer_mood"] = a.CustomerMood.ToString();
                record.Metrics["frustration"] = a.Frustration.ToString();
                record.Metrics["churn_risk"] = a.ChurnRisk.ToString();
                record.Metrics["trust_in_agent"] = a.TrustInAgent.ToString();
                record.Metrics["call_effectiveness"] = a.CallEffectiveness.ToString();
            }
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

    public async Task<IReadOnlyList<OutreachResult>> ListRecentAsync(int limit = 50) =>
        (await _store.ListRecentAsync(Math.Clamp(limit, 1, 200))).Select(r => r.ToResult()).ToList();

    public async Task<IReadOnlyList<OutreachResult>> GetCustomerTimelineAsync(string customerId) =>
        (await _store.ListByCustomerAsync(customerId)).Select(r => r.ToResult()).ToList();

    public async Task<IReadOnlyList<CustomerSummary>> ListCustomersAsync()
    {
        var all = await _store.ListRecentAsync(500);
        return all.GroupBy(r => r.CustomerId).Select(g => new CustomerSummary(
            CustomerId: g.Key,
            Name: g.Select(x => x.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
            Phone: g.Select(x => x.Phone).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
            Email: g.Select(x => x.Email).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)),
            Channels: g.Select(x => x.Channel).Distinct().ToArray(),
            OutreachCount: g.Count(),
            LastActivity: g.Max(x => x.UpdatedUtc),
            LastOutcome: g.OrderByDescending(x => x.UpdatedUtc).Select(x => x.Outcome).FirstOrDefault()))
            .OrderByDescending(c => c.LastActivity).ToList();
    }

    public IReadOnlyList<PersonaSummary> ListPersonas() =>
        s_personas.Value.Select(p => new PersonaSummary(p.Id, p.Label, p.Description, p.LanguageCode)).ToList();

    /// <summary>
    /// Block until the outreach reaches a terminal status or the timeout elapses, so an agent
    /// can await a voice call or an SMS reply without running its own poll loop.
    /// </summary>
    public async Task<OutreachResult?> WaitForResultAsync(string id, int timeoutSeconds, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 600));
        while (true)
        {
            var record = await _store.FindByIdAsync(id);
            if (record is null) return null;
            if (record.Status is "completed" or "failed" or "no_answer" || DateTimeOffset.UtcNow >= deadline)
                return record.ToResult();
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    public IReadOnlyList<ChannelCapability> ListChannels() => new[]
    {
        new ChannelCapability("voice", true, true, true, null, "Global reach incl. Danish numbers."),
        new ChannelCapability("sms", SmsEnabled, SmsEnabled, !string.IsNullOrWhiteSpace(_smsNumber), _mcFrom ?? _smsNumber ?? _smsSenderId,
            MessagingConnectEnabled
                ? $"SMS via Azure Communication Services Messaging Connect (partner: {_mcPartner}), afsender '{_mcFrom}'. Global rækkevidde inkl. +45; branded alfanumerisk afsender er en-vejs."
                : SmsEnabled ? $"To-vejs SMS til US/Canada via {_smsNumber}. Levering til andre lande (fx +45) kræver Messaging Connect." : "Not configured."),
        new ChannelCapability("email", EmailEnabled, EmailEnabled, GraphEmailEnabled, GraphEmailEnabled ? _graphSender : _emailSender,
            _preferAcsDelivery && _emailClient is not null
                ? $"Afsendes via ACS Email ({_emailSender}) for p\u00e5lidelig ekstern levering; svar g\u00e5r til '{_graphSender}' og opsamles automatisk."
                : GraphEmailEnabled
                    ? $"E-mail via Microsoft Graph fra postkassen '{_graphSender}'. Udg\u00e5ende globalt; kundesvar lander i postkassen, s\u00e5 to-vejs virker uden ACS-dom\u00e6ne."
                    : EmailEnabled ? "Outbound global. Inbound reply capture requires a custom domain." : "Not configured.")
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

    private static readonly Lazy<IReadOnlyList<Persona>> s_personas = new(() =>
    {
        try { return PersonaStore.LoadAll(); } catch { return Array.Empty<Persona>(); }
    });

    /// <summary>
    /// Draft the outbound SMS/email body with Azure OpenAI, steered by the persona system prompt,
    /// the named intent and any caller-supplied context. Returns null if drafting is unavailable,
    /// so the caller falls back to the canned per-intent text.
    /// </summary>
    private async Task<string?> DraftMessageAsync(string channel, OutreachRequest req)
    {
        if (_draftClient is null) return null;

        var personaPrompt = s_personas.Value.FirstOrDefault(p => p.Id == req.Persona)?.Prompt;
        var locale = string.IsNullOrWhiteSpace(req.Locale) ? "da-DK" : req.Locale!;
        var channelRules = channel == "sms"
            ? "Skriv en SMS: maks. 2 korte sætninger, under 320 tegn, ingen emojis, ingen markdown, ingen links medmindre de står i konteksten."
            : "Skriv en kort e-mailtekst: 2-4 sætninger i almindelig tekst, ingen emojis, ingen markdown, ingen emnelinje (den sættes separat).";

        var system = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(personaPrompt))
            system.AppendLine(personaPrompt).AppendLine();
        system.AppendLine($"Du skriver udgående kundekommunikation for Norlys på sproget {locale}.");
        system.AppendLine(channelRules);
        system.AppendLine("Skriv KUN selve beskeden — ingen forklaring, ingen anførselstegn omkring teksten.");

        var user = new StringBuilder();
        user.AppendLine($"Formål (intent): {req.Intent ?? "generel kontakt"}");
        if (!string.IsNullOrWhiteSpace(req.Customer.Name)) user.AppendLine($"Kundens navn: {req.Customer.Name}");
        if (req.Context is { Count: > 0 })
            foreach (var kv in req.Context) user.AppendLine($"{kv.Key}: {kv.Value}");

        try
        {
            var completion = await _draftClient.CompleteChatAsync(new ChatMessage[]
            {
                new SystemChatMessage(system.ToString()),
                new UserChatMessage(user.ToString())
            });
            var text = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text?.Trim() : null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            _logger.LogInformation("Drafted {Channel} body for intent {Intent} ({Chars} chars)", channel, req.Intent, text!.Length);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Message drafting failed for {Channel}; falling back to default text", channel);
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Lowercase emails, trim everything — reply matching must not depend on sender casing.</summary>
    private static string? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var a = address.Trim();
        return a.Contains('@') ? a.ToLowerInvariant() : a;
    }

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
