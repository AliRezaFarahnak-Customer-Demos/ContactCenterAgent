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
        _smsNumber = config["AcsSmsNumber"];
        // Optional alphanumeric sender ID. NOT enabled on this US-data-location resource
        // (ACS returns 401), so it stays empty unless a registered sender is explicitly configured.
        _smsSenderId = config["Acs:SmsSenderId"];
        _emailSender = config["Email:SenderAddress"];
        // Messaging Connect partner API key (Infobip, scope sms:message:send). The sender/number below
        // MUST be one Infobip has provisioned AND synced back to this ACS resource, otherwise ACS 401s.
        // Empty strings are treated as unset so a placeholder in appsettings.json stays inert.
        _mcApiKey = config["MessagingConnect:ApiKey"];
        var partner = config["MessagingConnect:Partner"];
        _mcPartner = string.IsNullOrWhiteSpace(partner) ? "infobip" : partner!;
        var mcSender = config["MessagingConnect:Sender"];
        _mcFrom = string.IsNullOrWhiteSpace(mcSender) ? _smsSenderId : mcSender;

        var graphSender = config["Graph:SenderAddress"];
        _graphSender = string.IsNullOrWhiteSpace(graphSender) ? null : graphSender!.Trim();

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
                    if (!SmsEnabled || string.IsNullOrWhiteSpace(req.Customer.Phone))
                        throw new InvalidOperationException("SMS-kanalen er ikke konfigureret, eller der mangler et telefonnummer.");
                    var toSms = req.Customer.Phone!.Trim();

                    // Azure-native Messaging Connect reaches Denmark (+45) and 190+ countries — prefer it when configured.
                    if (MessagingConnectEnabled)
                    {
                        await SendViaMessagingConnectAsync(toSms, messageText);
                        record.Interactions.Add(new Interaction { Channel = "sms", Direction = "outbound", Text = messageText });
                        // Mark awaiting_reply so a reply (real two-way long code, or a simulated demo reply) threads here.
                        record.Status = "awaiting_reply";
                        break;
                    }

                    // The only sender authorized on this US-data-location resource is the toll-free
                    // NUMBER; the alphanumeric sender ID returns 401 here, so prefer the number.
                    string smsFrom;
                    if (!string.IsNullOrWhiteSpace(_smsNumber)) smsFrom = _smsNumber!;
                    else if (!string.IsNullOrWhiteSpace(_smsSenderId)) smsFrom = _smsSenderId!;
                    else throw new InvalidOperationException("Ingen SMS-afsender konfigureret.");
                    // Replies only route back for US/CA (+1) on our toll-free; other countries are one-way.
                    var oneWay = !toSms.StartsWith("+1");
                    Azure.Response<SmsSendResult> smsResp;
                    try
                    {
                        smsResp = await _smsClient!.SendAsync(from: smsFrom, to: toSms, message: messageText);
                    }
                    catch (Azure.RequestFailedException rfe)
                    {
                        throw new InvalidOperationException(
                            $"SMS kunne ikke sendes fra '{smsFrom}' (HTTP {rfe.Status}). " +
                            (toSms.StartsWith("+1") ? "" : "Dette US-baserede ACS-resource sender to-vejs SMS til US/Canada; SMS til andre lande (fx +45) kræver et lokalt afsendernummer."));
                    }
                    if (!smsResp.Value.Successful)
                        throw new InvalidOperationException($"SMS afvist af ACS (HTTP {smsResp.Value.HttpStatusCode}): {smsResp.Value.ErrorMessage}");
                    record.Interactions.Add(new Interaction { Channel = "sms", Direction = "outbound", Text = messageText });
                    record.Status = oneWay ? "completed" : "awaiting_reply";
                    break;

                case "email":
                    if (!EmailEnabled || string.IsNullOrWhiteSpace(req.Customer.Email))
                        throw new InvalidOperationException("Email channel not available or no email address provided.");
                    var subject = req.Context is not null && req.Context.TryGetValue("subject", out var s) ? s : "Norlys – vi vil gerne i kontakt";
                    // Graph sends from a real mailbox and gets real replies — prefer it when configured.
                    if (GraphEmailEnabled)
                        await SendViaGraphAsync(req.Customer.Email!, subject, messageText);
                    else
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

    public IReadOnlyList<ChannelCapability> ListChannels() => new[]
    {
        new ChannelCapability("voice", true, true, true, null, "Global reach incl. Danish numbers."),
        new ChannelCapability("sms", SmsEnabled, SmsEnabled, !string.IsNullOrWhiteSpace(_smsNumber), _mcFrom ?? _smsNumber ?? _smsSenderId,
            MessagingConnectEnabled
                ? $"SMS via Azure Communication Services Messaging Connect (partner: {_mcPartner}), afsender '{_mcFrom}'. Global rækkevidde inkl. +45; branded alfanumerisk afsender er en-vejs."
                : SmsEnabled ? $"To-vejs SMS til US/Canada via {_smsNumber}. Levering til andre lande (fx +45) kræver Messaging Connect." : "Not configured."),
        new ChannelCapability("email", EmailEnabled, EmailEnabled, GraphEmailEnabled, GraphEmailEnabled ? _graphSender : _emailSender,
            GraphEmailEnabled
                ? $"E-mail via Microsoft Graph fra postkassen '{_graphSender}'. Udgående globalt; kundesvar lander i postkassen, så to-vejs virker uden ACS-domlæne."
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
