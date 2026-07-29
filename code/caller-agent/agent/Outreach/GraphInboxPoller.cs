using Azure.Core;
using Azure.Identity;
using System.Text;
using System.Text.Json;

namespace CallerAgent.Outreach;

/// <summary>
/// Polls a Microsoft 365 mailbox for customer email replies and feeds them into the SAME
/// inbound path as the ACS Event Grid webhook, so email is genuinely two-way without
/// attaching a custom domain to ACS.
///
/// Inert unless <c>Graph:SenderAddress</c> is set. Auth is DefaultAzureCredential (the
/// container app's managed identity in Azure, az login locally); that identity needs the
/// Graph application permission <c>Mail.ReadWrite</c> — see scripts/grant-graph-mail-permissions.ps1.
///
/// Dedupe strategy: only unread messages are read, and each is marked read once handled.
/// That survives restarts without persisting a cursor.
/// </summary>
public sealed class GraphInboxPoller : BackgroundService
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly OutreachService _outreach;
    private readonly ILogger<GraphInboxPoller> _logger;
    private readonly TokenCredential _credential = new DefaultAzureCredential();
    private readonly string? _mailbox;
    private readonly TimeSpan _interval;

    public GraphInboxPoller(OutreachService outreach, IConfiguration config, ILogger<GraphInboxPoller> logger)
    {
        _outreach = outreach;
        _logger = logger;

        var mailbox = config["Graph:SenderAddress"];
        _mailbox = string.IsNullOrWhiteSpace(mailbox) ? null : mailbox.Trim();
        _interval = TimeSpan.FromSeconds(Math.Max(10, config.GetValue<int?>("Graph:PollSeconds") ?? 30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_mailbox is null)
        {
            _logger.LogInformation("Graph inbox polling disabled (Graph:SenderAddress not set).");
            return;
        }

        _logger.LogInformation("Graph inbox polling {Mailbox} every {Seconds}s", _mailbox, _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph inbox poll failed; retrying next interval");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), ct);

        var url = $"{GraphBase}/users/{Uri.EscapeDataString(_mailbox!)}/mailFolders/inbox/messages" +
                  "?$filter=isRead%20eq%20false&$top=10&$orderby=receivedDateTime%20asc" +
                  "&$select=id,subject,bodyPreview,from";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.Token}");

        var resp = await s_http.SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Graph inbox read failed (HTTP {Status}): {Body}",
                (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            return;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("value", out var messages)) return;

        foreach (var m in messages.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;

            var from = m.TryGetProperty("from", out var f)
                       && f.TryGetProperty("emailAddress", out var addr)
                       && addr.TryGetProperty("address", out var a)
                ? a.GetString() : null;
            var subject = m.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";
            var body = m.TryGetProperty("bodyPreview", out var b) ? b.GetString() ?? "" : "";

            if (!string.IsNullOrWhiteSpace(from))
            {
                _logger.LogInformation("Inbound email via Graph from {From}", from);
                await _outreach.HandleInboundEmailAsync(from!, subject, body);
            }

            await MarkReadAsync(id!, token.Token, ct);
        }
    }

    private async Task MarkReadAsync(string messageId, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch,
            $"{GraphBase}/users/{Uri.EscapeDataString(_mailbox!)}/messages/{messageId}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Content = new StringContent("{\"isRead\":true}", Encoding.UTF8, "application/json");

        var resp = await s_http.SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("Could not mark message {Id} read (HTTP {Status}); it may be reprocessed",
                messageId, (int)resp.StatusCode);
    }
}
