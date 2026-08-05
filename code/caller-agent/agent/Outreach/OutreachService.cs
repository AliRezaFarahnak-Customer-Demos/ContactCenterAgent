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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private readonly bool _autoReply;
    private const int MaxAiReplies = 12;
    private readonly TokenCredential _credential = new DefaultAzureCredential();
    // Drafts the SMS/email body from intent + persona system prompt + context when the caller
    // supplies no literal message. Same Azure OpenAI resource the voice + analysis paths use.
    private readonly ChatClient? _draftClient;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationLocks = new(StringComparer.Ordinal);
    private readonly string[] _callbackAllowedHosts;
    private readonly bool _allowPrivateCallbackTargets;
    private readonly int _callbackMaxAttempts;
    private readonly bool _emailSubjectTag;
    // Lets a queued callback be delivered at once instead of waiting for the next dispatcher tick.
    private readonly SemaphoreSlim _callbackSignal = new(0, 1);
    private const int MaxInboundChars = 4000;
    private const int MaxTrackedIds = 100;

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
        // Demo default ON: the agent answers written replies itself and can act on them.
        _autoReply = config.GetValue<bool?>("Outreach:AutoReply") ?? true;
        _callbackAllowedHosts = config.GetSection("Outreach:CallbackAllowedHosts").Get<string[]>() ?? Array.Empty<string>();
        _allowPrivateCallbackTargets = config.GetValue<bool>("Outreach:AllowPrivateCallbackTargets", false);
        _callbackMaxAttempts = Math.Clamp(config.GetValue<int>("Outreach:CallbackMaxAttempts", 8), 1, 20);
        _emailSubjectTag = config.GetValue<bool>("Outreach:EmailSubjectTag", true);

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
        var clientRequestId = NullIfEmpty(req.ClientRequestId);
        var requestFingerprint = clientRequestId is null ? null : CreateRequestFingerprint(req);
        SemaphoreSlim? requestLock = null;
        if (clientRequestId is not null)
        {
            requestLock = _operationLocks.GetOrAdd($"start:{clientRequestId}", _ => new SemaphoreSlim(1, 1));
            await requestLock.WaitAsync();
        }

        try
        {
            if (clientRequestId is not null)
            {
                var existing = await _store.FindByClientRequestIdAsync(clientRequestId);
                if (existing is not null)
                {
                    if (!string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                        throw new OutreachConflictException($"clientRequestId '{clientRequestId}' was already used for a different outreach request.");
                    return existing.ToResult();
                }
            }

            return await StartCoreAsync(req, clientRequestId, requestFingerprint);
        }
        finally
        {
            requestLock?.Release();
        }
    }

    private async Task<OutreachResult> StartCoreAsync(
        OutreachRequest req, string? clientRequestId, string? requestFingerprint)
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
            CallbackStatus = string.IsNullOrWhiteSpace(req.CallbackUrl) ? "not_requested" : "waiting",
            ClientRequestId = clientRequestId,
            RequestFingerprint = requestFingerprint,
            Persona = req.Persona,
            Subject = req.Context is not null && req.Context.TryGetValue("subject", out var subj) ? subj : null,
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
                    var baseSubject = req.Context is not null && req.Context.TryGetValue("subject", out var s) ? s : "Norlys – vi vil gerne i kontakt";
                    var subject = WithThreadTag(baseSubject, record.Id);
                    record.Subject = subject;
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

        if (IsTerminal(record.Status)) MarkCallbackPending(record);
        await _store.UpsertAsync(record);
        _telemetry?.TrackEvent("OutreachStarted", new Dictionary<string, string>
        {
            { "outreach.channel", channel },
            { "outreach.status", record.Status },
            { "outreach.id", record.Id }
        });
        if (record.CallbackStatus == "pending") SignalCallbackWork();
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
    public async Task<OutreachResult?> SendFollowUpAsync(
        string outreachId, string message, string? channel = null, string? subject = null,
        string? clientRequestId = null)
    {
        clientRequestId = NullIfEmpty(clientRequestId);
        var operationLock = _operationLocks.GetOrAdd($"outreach:{outreachId}", _ => new SemaphoreSlim(1, 1));
        await operationLock.WaitAsync();
        var signalCallback = false;
        try
        {
            var record = await _store.FindByIdAsync(outreachId);
            if (record is null) return null;
            if (clientRequestId is not null && record.ProcessedRequestIds.Contains(clientRequestId, StringComparer.Ordinal))
                return record.ToResult();

            // Reserve the key before delivery. This chooses at-most-once behavior if the process
            // stops between the provider accepting the message and our final persistence write.
            if (clientRequestId is not null)
            {
                TrackId(record.ProcessedRequestIds, clientRequestId);
                await _store.UpsertAsync(record);
            }

            // Voice can't carry a written follow-up, so fall back to whichever text channel we can reach.
            var ch = (channel ?? (record.Channel == "voice"
                ? (SmsEnabled && !string.IsNullOrWhiteSpace(record.Phone) ? "sms" : "email")
                : record.Channel)).Trim().ToLowerInvariant();
            var subj = string.IsNullOrWhiteSpace(subject)
                ? (string.IsNullOrWhiteSpace(record.Subject) ? "Norlys – opfølgning" : record.Subject!)
                : subject!;
            if (ch == "email") subj = WithThreadTag(subj, record.Id);

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

            if (IsTerminal(record.Status)) MarkCallbackPending(record);
            await _store.UpsertAsync(record);
            signalCallback = record.CallbackStatus == "pending";
            _telemetry?.TrackEvent("OutreachFollowUp", new Dictionary<string, string>
            {
                { "outreach.channel", ch }, { "outreach.status", record.Status }, { "outreach.id", record.Id }
            });
            return record.ToResult();
        }
        finally
        {
            operationLock.Release();
            if (signalCallback) SignalCallbackWork();
        }
    }

    // -----------------------------------------------------------------------
    // Inbound reply correlation
    // -----------------------------------------------------------------------
    public Task HandleInboundSmsAsync(string from, string message, string? messageId = null) =>
        HandleInboundCoreAsync("sms", from, "", message, messageId, null, createIfMissing: true);

    public Task HandleInboundEmailAsync(string from, string subject, string body, string? messageId = null) =>
        HandleInboundCoreAsync("email", from, subject, body, messageId, null, createIfMissing: true);

    /// <summary>
    /// Thread an emailed reply onto an existing outreach. A mailbox also receives unrelated
    /// human mail, so unlike SMS to a dedicated number, mail from an address we never contacted
    /// is ignored instead of being captured as a new customer. Returns true when it was threaded.
    /// </summary>
    public Task<bool> TryHandleInboundEmailAsync(
        string from, string subject, string body, string? messageId = null, string? conversationId = null) =>
        HandleInboundCoreAsync("email", from, subject, body, messageId, conversationId, createIfMissing: false);

    private async Task<bool> HandleInboundCoreAsync(
        string channel, string from, string subject, string body,
        string? messageId, string? conversationId, bool createIfMissing)
    {
        var address = NormalizeAddress(from);
        if (string.IsNullOrWhiteSpace(address)) return false;

        var text = channel == "email"
            ? BuildInboundEmailText(subject, body)
            : Truncate(body.Trim(), MaxInboundChars);

        var target = await FindInboundTargetAsync(channel, address!, subject, conversationId);
        if (target is null)
        {
            // Unmatched inbound — still capture it so nothing is lost in the timeline.
            if (!createIfMissing) return false;
            target = new OutreachRecord
            {
                CustomerId = address!,
                Phone = channel == "sms" ? address : null,
                Email = channel == "email" ? address : null,
                Channel = channel,
                Status = "pending"
            };
            await _store.UpsertAsync(target);
        }

        // Serialized per outreach so two replies arriving together can't overwrite each other.
        var gate = _operationLocks.GetOrAdd($"outreach:{target.Id}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        var signalCallback = false;
        try
        {
            var record = await _store.FindByIdAsync(target.Id) ?? target;
            // A provider redelivery must not append the same message or trigger a second AI reply.
            if (messageId is not null && record.ProcessedInboundIds.Contains(messageId, StringComparer.Ordinal))
                return true;
            if (messageId is not null) TrackId(record.ProcessedInboundIds, messageId);
            if (!string.IsNullOrWhiteSpace(conversationId) && string.IsNullOrWhiteSpace(record.EmailConversationId))
                record.EmailConversationId = conversationId;

            await AppendInboundAsync(record, channel, text);
            signalCallback = record.CallbackStatus == "pending";
            return true;
        }
        finally
        {
            gate.Release();
            if (signalCallback) SignalCallbackWork();
        }
    }

    /// <summary>
    /// Resolve which outreach an inbound message belongs to. Email carries a durable reference
    /// (subject tag, then Graph conversation id); SMS has no thread identifier, so it can only fall
    /// back to the most recent thread for that number.
    /// </summary>
    private async Task<OutreachRecord?> FindInboundTargetAsync(
        string channel, string address, string subject, string? conversationId)
    {
        if (channel == "email")
        {
            var reference = ExtractThreadTag(subject);
            if (reference is not null)
            {
                var byReference = await _store.FindByIdPrefixAsync(reference);
                if (byReference is not null) return byReference;
            }
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                var byConversation = await _store.FindByEmailConversationIdAsync(conversationId!);
                if (byConversation is not null) return byConversation;
            }
        }

        return await _store.FindLatestForAddressAsync(address);
    }

    private async Task AppendInboundAsync(OutreachRecord record, string channel, string text)
    {
        record.Interactions.Add(new Interaction { Channel = channel, Direction = "inbound", Text = text });
        record.Reply = text;
        record.Status = "completed";
        // A new message reopens the case, so a previous close_case must not short-circuit this round.
        record.Outcome = null;

        // Agentic continuation: answer in the same thread, and act (place a call, close the case).
        // Skipped when we'd be replying to our own mailbox, which would loop forever.
        var selfSend = _graphSender is not null &&
                       string.Equals(record.Email, _graphSender, StringComparison.OrdinalIgnoreCase);
        if (_autoReply && channel is "sms" or "email" && !selfSend && record.AiReplies < MaxAiReplies)
        {
            var reply = await RunThreadAgentAsync(record, channel);
            if (!string.IsNullOrWhiteSpace(reply))
            {
                var subject = string.IsNullOrWhiteSpace(record.Subject) ? "Norlys" : record.Subject!;
                if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)) subject = $"Re: {subject}";
                if (channel == "email") subject = WithThreadTag(subject, record.Id);
                try
                {
                    await DeliverAsync(channel, record.Phone, record.Email, reply!, subject);
                    record.AiReplies++;
                    record.Interactions.Add(new Interaction
                    {
                        Channel = channel,
                        Direction = "outbound",
                        Text = channel == "email" ? $"{subject}\n\n{reply}" : reply!
                    });
                    // Keep the thread open unless the agent closed the case itself.
                    record.Status = record.Outcome == "resolved" ? "completed" : "awaiting_reply";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-reply delivery failed for outreach {Id}", record.Id);
                }
            }
        }

        if (IsTerminal(record.Status))
        {
            await AnalyzeWrittenThreadAsync(record);
            MarkCallbackPending(record);
        }

        await _store.UpsertAsync(record);

        _telemetry?.TrackEvent("OutreachInboundReply", new Dictionary<string, string>
        {
            { "outreach.channel", channel }, { "outreach.id", record.Id }, { "outreach.status", record.Status }
        });
    }

    // -----------------------------------------------------------------------
    // Voice finalization (called on CallDisconnected, then enriched with summary)
    // -----------------------------------------------------------------------
    public async Task FinalizeVoiceAsync(string contextId, CaseSummary? summary)
    {
        var located = await _store.FindByContextIdAsync(contextId);
        if (located is null) return;

        // Disconnect and case-summary finalization can arrive together for the same call.
        var gate = _operationLocks.GetOrAdd($"outreach:{located.Id}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        var signalCallback = false;
        try
        {
            var record = await _store.FindByIdAsync(located.Id) ?? located;
            ApplyVoiceSummary(record, summary);

            record.Status = summary?.Outcome switch
            {
                "no_answer" => "no_answer",
                _ => "completed"
            };

            // Only the summary-bearing finalization is worth pushing; the bare disconnect would
            // deliver a result with no case data and mark the callback done.
            if (summary is not null) MarkCallbackPending(record);
            await _store.UpsertAsync(record);
            signalCallback = record.CallbackStatus == "pending";
        }
        finally
        {
            gate.Release();
            if (signalCallback) SignalCallbackWork();
        }
    }

    private static void ApplyVoiceSummary(OutreachRecord record, CaseSummary? summary)
    {
        if (summary is null) return;

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

    public Task<int> ClearAllAsync() => _store.DeleteAllAsync();

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
            if (IsTerminal(record.Status) || DateTimeOffset.UtcNow >= deadline)
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

    private static string CreateRequestFingerprint(OutreachRequest request)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            request.Customer,
            Channel = request.Channel.Trim().ToLowerInvariant(),
            request.Intent,
            request.Message,
            Context = request.Context?.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            request.CallbackUrl,
            request.Persona,
            request.Locale,
            request.Voice,
            request.VoiceStyle
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void TrackId(List<string> ids, string value)
    {
        ids.Add(value);
        if (ids.Count > MaxTrackedIds) ids.RemoveRange(0, ids.Count - MaxTrackedIds);
    }

    private static readonly Regex s_threadTag = new(@"\[sag:\s*([0-9a-fA-F]{8})\]", RegexOptions.Compiled);

    /// <summary>
    /// Stamps a short outreach reference into the subject. Mail clients keep the subject on reply,
    /// so this is what lets a customer with several open cases be threaded onto the right one.
    /// </summary>
    private string WithThreadTag(string subject, string outreachId)
    {
        if (!_emailSubjectTag) return subject;
        var reference = outreachId[..8];
        return subject.Contains(reference, StringComparison.OrdinalIgnoreCase)
            ? subject
            : $"{subject} [sag: {reference}]";
    }

    private static string? ExtractThreadTag(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var match = s_threadTag.Match(subject);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string BuildInboundEmailText(string subject, string body)
    {
        var cleaned = StripQuotedReply(body);
        return string.IsNullOrWhiteSpace(subject) ? cleaned : $"{subject}\n\n{cleaned}".Trim();
    }

    private static readonly string[] s_quoteMarkers =
    {
        "\n-----Original Message-----",
        "\n________________________________",
        "\nFrom: ",
        "\nFra: ",
        "\nSent: "
    };

    /// <summary>
    /// Drops the quoted history a mail client appends to a reply, so the thread agent reasons about
    /// what the customer actually wrote instead of replaying our own previous message back at itself.
    /// </summary>
    private static string StripQuotedReply(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var normalized = body.Replace("\r\n", "\n");

        var cut = normalized.Length;
        foreach (var marker in s_quoteMarkers)
        {
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && index < cut) cut = index;
        }

        var kept = normalized[..cut].Split('\n')
            .Reverse()
            .SkipWhile(line => line.TrimStart().StartsWith('>') || line.Trim().Length == 0)
            .Reverse();
        var text = string.Join('\n', kept).Trim();
        if (text.Length == 0) text = normalized.Trim();   // the reply was nothing but quoted history
        return Truncate(text, MaxInboundChars);
    }

    private static readonly BinaryData s_writtenAnalysisSchema = BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "summary": { "type": "string" },
        "outcome": { "type": "string", "enum": ["resolved", "follow_up_needed", "declined", "no_response"] },
        "topics": { "type": "array", "items": { "type": "string" } },
        "collected": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": { "key": { "type": "string" }, "value": { "type": "string" } },
            "required": ["key", "value"],
            "additionalProperties": false
          }
        },
        "satisfaction": { "type": "integer" },
        "problem_solved": { "type": "integer" },
        "overall_sentiment": { "type": "integer" },
        "frustration": { "type": "integer" },
        "churn_risk": { "type": "integer" }
      },
      "required": ["summary", "outcome", "topics", "collected", "satisfaction", "problem_solved", "overall_sentiment", "frustration", "churn_risk"],
      "additionalProperties": false
    }
    """);

    /// <summary>
    /// Gives a closed SMS/email thread the same structured output a voice call produces, so the
    /// calling agent gets a summary, outcome, collected fields and sentiment on every channel.
    /// </summary>
    private async Task AnalyzeWrittenThreadAsync(OutreachRecord record)
    {
        if (_draftClient is null) return;
        var written = record.Interactions.Where(i => i.Channel != "voice").ToList();
        if (written.Count == 0 || !written.Any(i => i.Direction == "inbound")) return;

        var transcript = string.Join("\n", written.Select(i =>
            $"{(i.Direction == "outbound" ? "Norlys" : "Kunden")}: {i.Text}"));

        try
        {
            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "written_thread_analysis",
                    jsonSchema: s_writtenAnalysisSchema,
                    jsonSchemaIsStrict: true)
            };

            var completion = await _draftClient.CompleteChatAsync(new ChatMessage[]
            {
                new SystemChatMessage(
                    "Analysér en skriftlig kundedialog for Norlys og returner struktureret JSON. " +
                    "Alle scorer er heltal 0-6, hvor 3 er neutral. Skriv summary på dansk."),
                new UserChatMessage($"Formål: {record.Intent ?? "generel kontakt"}\n\nDialog:\n{transcript}")
            }, options);

            using var doc = JsonDocument.Parse(completion.Value.Content[0].Text);
            var root = doc.RootElement;

            record.Summary = root.GetProperty("summary").GetString();
            record.Outcome ??= root.GetProperty("outcome").GetString();
            record.Topics = root.GetProperty("topics").EnumerateArray()
                .Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToList();

            record.Collected ??= new Dictionary<string, string>();
            foreach (var item in root.GetProperty("collected").EnumerateArray())
            {
                var key = item.GetProperty("key").GetString();
                var value = item.GetProperty("value").GetString();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    record.Collected[key!] = value!;
            }

            record.Metrics ??= new Dictionary<string, string>();
            record.Metrics["sentiment_scale"] = "0-6 (3 = neutral)";
            foreach (var metric in new[] { "satisfaction", "problem_solved", "overall_sentiment", "frustration", "churn_risk" })
                record.Metrics[metric] = root.GetProperty(metric).GetInt32().ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Written-thread analysis failed for outreach {Id}", record.Id);
        }
    }

    /// <summary>
    /// Continue a written thread agentically: the record's interaction history IS the conversation
    /// session, replayed as chat turns, and the model gets tools so it can act — place a real voice
    /// call when the customer asks to be phoned, or close the case. Returns the reply to send.
    /// </summary>
    private async Task<string?> RunThreadAgentAsync(OutreachRecord record, string channel)
    {
        if (_draftClient is null) return null;

        var personaPrompt = s_personas.Value.FirstOrDefault(p => p.Id == record.Persona)?.Prompt;
        var channelRules = channel == "sms"
            ? "Svar som SMS: maks. 2 korte sætninger, under 320 tegn."
            : "Svar som en kort e-mail: 2-5 sætninger i almindelig tekst, ingen emnelinje.";

        var system = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(personaPrompt))
            system.AppendLine(personaPrompt).AppendLine();
        system.AppendLine("Du er Norlys' kundeserviceassistent og fører en skriftlig samtale med kunden på dansk.");
        system.AppendLine(channelRules);
        system.AppendLine("Ingen emojis, ingen markdown. Stil kun ét spørgsmål ad gangen.");
        system.AppendLine($"Sagens formål: {record.Intent ?? "generel kontakt"}.");
        if (!string.IsNullOrWhiteSpace(record.CustomerName)) system.AppendLine($"Kundens navn: {record.CustomerName}");
        if (!string.IsNullOrWhiteSpace(record.Phone)) system.AppendLine($"Kendt telefonnummer: {record.Phone}");
        if (record.Context is { Count: > 0 })
            foreach (var kv in record.Context) system.AppendLine($"{kv.Key}: {kv.Value}");
        system.AppendLine();
        system.AppendLine("VÆRKTØJER:");
        system.AppendLine("- Beder kunden om at blive ringet op, så brug place_phone_call. Har du ikke et nummer, så spørg om det først og ring i næste tur.");
        system.AppendLine("- Beder kunden om en SMS, så brug send_sms.");
        system.AppendLine("- Er sagen afsluttet, eller vil kunden ikke fortsætte, så brug close_case.");
        system.AppendLine("Du må gerne bruge flere værktøjer i samme tur.");
        system.AppendLine("Efter et værktøjskald skal du ALTID skrive en kort besked til kunden om hvad der nu sker. Lykkedes et værktøj ikke, så sig det ærligt.");

        var messages = new List<ChatMessage> { new SystemChatMessage(system.ToString()) };
        foreach (var i in record.Interactions.Where(x => x.Channel != "voice"))
        {
            if (i.Direction == "outbound") messages.Add(new AssistantChatMessage(i.Text));
            else messages.Add(new UserChatMessage(i.Text));
        }

        var options = new ChatCompletionOptions();
        options.Tools.Add(ChatTool.CreateFunctionTool("place_phone_call",
            "Ring til kunden med Norlys' AI-telefonagent. Brug kun når kunden har bedt om at blive ringet op OG du har et telefonnummer.",
            BinaryData.FromString("""
            {"type":"object","properties":{
              "phoneNumber":{"type":"string","description":"Kundens nummer i E.164, fx +4521858353"},
              "reason":{"type":"string","description":"Kort begrundelse for opkaldet"}},
             "required":["phoneNumber","reason"]}
            """)));
        options.Tools.Add(ChatTool.CreateFunctionTool("send_sms",
            "Send en SMS til kunden. Brug når kunden beder om en SMS-bekræftelse eller -påmindelse.",
            BinaryData.FromString("""
            {"type":"object","properties":{
              "phoneNumber":{"type":"string","description":"Kundens nummer i E.164"},
              "message":{"type":"string","description":"SMS-teksten, maks 320 tegn"}},
             "required":["phoneNumber","message"]}
            """)));
        options.Tools.Add(ChatTool.CreateFunctionTool("close_case",
            "Afslut sagen når formålet er opfyldt, eller kunden ikke vil fortsætte.",
            BinaryData.FromString("""
            {"type":"object","properties":{
              "summary":{"type":"string","description":"Kort resumé af udfaldet"},
              "collected":{"type":"object","description":"Felter kunden har oplyst","additionalProperties":{"type":"string"}}},
             "required":["summary"]}
            """)));

        try
        {
            // Two passes is enough: act, then speak about what happened.
            for (var turn = 0; turn < 2; turn++)
            {
                var completion = await _draftClient.CompleteChatAsync(messages, options);
                var value = completion.Value;

                if (value.ToolCalls.Count == 0)
                {
                    var text = value.Content.Count > 0 ? value.Content[0].Text?.Trim() : null;
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }

                messages.Add(new AssistantChatMessage(value));
                foreach (var call in value.ToolCalls)
                {
                    var result = await ExecuteThreadToolAsync(record, call.FunctionName, call.FunctionArguments);
                    messages.Add(new ToolChatMessage(call.Id, result));
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thread agent failed for outreach {Id}", record.Id);
            return null;
        }
    }

    private async Task<string> ExecuteThreadToolAsync(OutreachRecord record, string name, BinaryData args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.ToString());
            switch (name)
            {
                case "place_phone_call":
                    {
                        var phone = doc.RootElement.TryGetProperty("phoneNumber", out var p) ? p.GetString() : null;
                        var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : "opfølgning";
                        phone = NormalizeAddress(phone) ?? record.Phone;
                        if (string.IsNullOrWhiteSpace(phone)) return "Intet telefonnummer — spørg kunden om nummeret.";
                        if (!phone.StartsWith('+')) phone = "+" + phone.TrimStart('+');
                        if (VoicePlacer is null) return "Telefonkanalen er ikke tilgængelig.";

                        record.Phone = phone;
                        // VoicePlacer reads the system prompt off the record, so hand the voice agent
                        // the written thread — same customer session, different channel.
                        record.Message = BuildVoicePromptFromThread(record, reason!);
                        record.ContextId = await VoicePlacer(record);
                        record.Interactions.Add(new Interaction
                        {
                            Channel = "voice",
                            Direction = "outbound",
                            Text = $"Udgående opkald til {phone} påbegyndt ({reason})."
                        });
                        _logger.LogInformation("Thread agent placed a call to {Phone} for outreach {Id}", phone, record.Id);
                        return $"Opkald til {phone} er startet.";
                    }

                case "send_sms":
                    {
                        var smsTo = doc.RootElement.TryGetProperty("phoneNumber", out var sp) ? sp.GetString() : null;
                        var smsText = doc.RootElement.TryGetProperty("message", out var sm) ? sm.GetString() : null;
                        smsTo = NormalizeAddress(smsTo) ?? record.Phone;
                        if (string.IsNullOrWhiteSpace(smsTo)) return "Intet telefonnummer — spørg kunden om nummeret.";
                        if (!smsTo.StartsWith('+')) smsTo = "+" + smsTo.TrimStart('+');
                        if (string.IsNullOrWhiteSpace(smsText)) return "Ingen SMS-tekst angivet.";

                        try
                        {
                            await DeliverAsync("sms", smsTo, null, smsText!, "");
                            record.Phone ??= smsTo;
                            record.Interactions.Add(new Interaction { Channel = "sms", Direction = "outbound", Text = smsText! });
                            _logger.LogInformation("Thread agent sent an SMS to {Phone} for outreach {Id}", smsTo, record.Id);
                            return $"SMS sendt til {smsTo}.";
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Thread agent SMS to {Phone} failed", smsTo);
                            return $"SMS kunne ikke sendes: {ex.Message}";
                        }
                    }

                case "close_case":
                    {
                        if (doc.RootElement.TryGetProperty("summary", out var s))
                            record.Summary = s.GetString();
                        if (doc.RootElement.TryGetProperty("collected", out var c) && c.ValueKind == JsonValueKind.Object)
                        {
                            record.Collected ??= new Dictionary<string, string>();
                            foreach (var p in c.EnumerateObject())
                            {
                                var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                                if (!string.IsNullOrWhiteSpace(v)) record.Collected[p.Name] = v!;
                            }
                        }
                        record.Outcome = "resolved";
                        return "Sagen er markeret som afsluttet.";
                    }

                default:
                    return $"Ukendt værktøj '{name}'.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thread tool {Tool} failed for outreach {Id}", name, record.Id);
            return "Værktøjet fejlede.";
        }
    }

    private static string BuildVoicePromptFromThread(OutreachRecord record, string reason)
    {
        var persona = s_personas.Value.FirstOrDefault(p => p.Id == record.Persona)?.Prompt;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(persona)) sb.AppendLine(persona).AppendLine();
        sb.AppendLine($"Du ringer til {record.CustomerName ?? "kunden"} som opfølgning på en skriftlig samtale.");
        sb.AppendLine($"Kunden bad selv om at blive ringet op. Begrundelse: {reason}");
        sb.AppendLine($"Sagens formål: {record.Intent ?? "generel kontakt"}");
        if (record.Context is { Count: > 0 })
            foreach (var kv in record.Context) sb.AppendLine($"{kv.Key}: {kv.Value}");
        sb.AppendLine();
        sb.AppendLine("Tidligere skriftlig samtale:");
        foreach (var i in record.Interactions.Where(x => x.Channel != "voice"))
            sb.AppendLine($"- {(i.Direction == "outbound" ? "Norlys" : "Kunden")}: {i.Text}");
        sb.AppendLine();
        sb.AppendLine("Sig kort hvorfor du ringer, og hjælp kunden med at afslutte sagen.");
        return sb.ToString();
    }


    /// <summary>Lowercase emails, trim everything — reply matching must not depend on sender casing.</summary>
    private static string? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var a = address.Trim();
        return a.Contains('@') ? a.ToLowerInvariant() : a;
    }

    public async Task<int> DispatchDueCallbacksAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var dispatched = 0;
        foreach (var record in await _store.ListPendingCallbacksAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.CallbackNextAttemptUtc is { } next && next > now) continue;
            await TryDeliverCallbackAsync(record.Id, cancellationToken);
            dispatched++;
        }
        return dispatched;
    }

    /// <summary>Returns as soon as a callback is queued, otherwise after the poll interval.</summary>
    public Task WaitForCallbackWorkAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _callbackSignal.WaitAsync(timeout, cancellationToken);

    public async Task<OutreachResult?> RetryCallbackAsync(string outreachId, CancellationToken cancellationToken = default)
    {
        var operationLock = _operationLocks.GetOrAdd($"outreach:{outreachId}", _ => new SemaphoreSlim(1, 1));
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var record = await _store.FindByIdAsync(outreachId);
            if (record is null) return null;
            if (string.IsNullOrWhiteSpace(record.CallbackUrl))
                throw new InvalidOperationException("This outreach has no callbackUrl.");
            if (!IsTerminal(record.Status))
                throw new InvalidOperationException("The outreach is not in a terminal state yet.");

            record.CallbackStatus = "pending";
            record.CallbackAttempts = 0;
            record.CallbackNextAttemptUtc = DateTimeOffset.UtcNow;
            record.CallbackDeliveredUtc = null;
            record.CallbackLastError = null;
            await _store.UpsertAsync(record);
        }
        finally
        {
            operationLock.Release();
        }

        await TryDeliverCallbackAsync(outreachId, cancellationToken);
        return (await _store.FindByIdAsync(outreachId))?.ToResult();
    }

    /// <summary>
    /// Flags the record for delivery without doing I/O, so callers can persist it in their own write.
    /// Delivery itself runs on the dispatcher, which keeps it off the per-outreach lock held here.
    /// </summary>
    private static void MarkCallbackPending(OutreachRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.CallbackUrl) || record.CallbackStatus == "delivered") return;
        record.CallbackStatus = "pending";
        record.CallbackNextAttemptUtc = DateTimeOffset.UtcNow;
    }

    private void SignalCallbackWork()
    {
        try { _callbackSignal.Release(); }
        catch (SemaphoreFullException) { /* a pass is already queued */ }
    }

    private async Task TryDeliverCallbackAsync(string outreachId, CancellationToken cancellationToken = default)
    {
        var operationLock = _operationLocks.GetOrAdd($"outreach:{outreachId}", _ => new SemaphoreSlim(1, 1));
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var record = await _store.FindByIdAsync(outreachId);
            if (record is null || record.CallbackStatus != "pending" || string.IsNullOrWhiteSpace(record.CallbackUrl)) return;
            if (record.CallbackNextAttemptUtc is { } next && next > DateTimeOffset.UtcNow) return;

            record.CallbackAttempts++;
            try
            {
                var target = await ValidateCallbackTargetAsync(record.CallbackUrl, cancellationToken);
                var json = JsonSerializer.Serialize(record.ToResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                using var handler = CreatePinnedCallbackHandler(target);
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                using var request = new HttpRequestMessage(HttpMethod.Post, target.Uri)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException(
                        $"Callback returned HTTP {(int)response.StatusCode}: {Truncate(detail, 512)}");
                }

                record.CallbackStatus = "delivered";
                record.CallbackDeliveredUtc = DateTimeOffset.UtcNow;
                record.CallbackNextAttemptUtc = null;
                record.CallbackLastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record.CallbackLastError = Truncate(ex.Message, 1000);
                if (record.CallbackAttempts >= _callbackMaxAttempts || ex is CallbackTargetException)
                {
                    record.CallbackStatus = "dead_lettered";
                    record.CallbackNextAttemptUtc = null;
                }
                else
                {
                    var delaySeconds = Math.Min(300, 5 * Math.Pow(2, record.CallbackAttempts - 1));
                    record.CallbackNextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                }
                _logger.LogWarning(ex,
                    "Callback attempt {Attempt}/{MaxAttempts} failed for outreach {Id}",
                    record.CallbackAttempts, _callbackMaxAttempts, record.Id);
            }
            await _store.UpsertAsync(record);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<CallbackTarget> ValidateCallbackTargetAsync(string callbackUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new CallbackTargetException("callbackUrl must be an absolute HTTPS URL without credentials or a fragment.");

        if (_callbackAllowedHosts.Length > 0 && !_callbackAllowedHosts.Any(pattern => HostMatches(uri.DnsSafeHost, pattern)))
            throw new CallbackTargetException($"Callback host '{uri.DnsSafeHost}' is not in Outreach:CallbackAllowedHosts.");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Callback host could not be resolved: {ex.Message}", ex);
        }
        if (addresses.Length == 0) throw new InvalidOperationException("Callback host resolved to no addresses.");
        if (!_allowPrivateCallbackTargets && addresses.Any(IsPrivateOrReserved))
            throw new CallbackTargetException("callbackUrl resolves to a private, loopback, link-local or reserved address.");

        return new CallbackTarget(uri, addresses[0]);
    }

    private static SocketsHttpHandler CreatePinnedCallbackHandler(CallbackTarget target) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(target.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target.Address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    private static bool HostMatches(string host, string pattern)
    {
        pattern = pattern.Trim().TrimEnd('.');
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                && host.Length > pattern.Length - 1;
        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (bytes[0] & 0xFE) == 0xFC;
        return bytes[0] is 0 or 10 or 127
            || bytes[0] >= 224
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 198 && bytes[1] is 18 or 19);
    }

    private static bool IsTerminal(string status) =>
        status is "completed" or "failed" or "no_answer";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record CallbackTarget(Uri Uri, IPAddress Address);

    private sealed class CallbackTargetException(string message) : Exception(message);
}
