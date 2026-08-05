using Azure.Identity;
using CallerAgent.Outreach;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CallerAgent.Tests;

/// <summary>
/// Covers the outreach behaviour that is hard to verify by hand on a live demo: retry safety,
/// reply correlation and callback target validation. Everything runs against the in-memory store
/// with no ACS, Graph or Cosmos configured, so no channel actually delivers.
/// </summary>
public class OutreachServiceTests
{
    private static (OutreachService Service, OutreachStore Store) CreateService(
        Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        var store = new OutreachStore(config, new DefaultAzureCredential(), NullLogger<OutreachStore>.Instance);
        return (new OutreachService(store, config, NullLogger<OutreachService>.Instance), store);
    }

    private static OutreachRequest VoiceRequest(string clientRequestId, string phone = "+4512345678") =>
        new(new OutreachCustomer("kunde-1042", "Mette", phone, null), "voice",
            Intent: "verify_address", ClientRequestId: clientRequestId);

    [Fact]
    public async Task RepeatedStart_WithSameClientRequestId_PlacesOnlyOneCall()
    {
        var (service, _) = CreateService();
        var placements = 0;
        service.VoicePlacer = _ =>
        {
            Interlocked.Increment(ref placements);
            return Task.FromResult<string?>("context-1");
        };

        var first = await service.StartAsync(VoiceRequest("case-1042"));
        var second = await service.StartAsync(VoiceRequest("case-1042"));

        Assert.Equal(first.OutreachId, second.OutreachId);
        Assert.Equal(1, placements);
    }

    [Fact]
    public async Task ConcurrentStarts_WithSameClientRequestId_PlaceOnlyOneCall()
    {
        var (service, _) = CreateService();
        var placements = 0;
        service.VoicePlacer = async _ =>
        {
            Interlocked.Increment(ref placements);
            await Task.Delay(20);
            return "context-1";
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.StartAsync(VoiceRequest("case-1042"))));

        Assert.Single(results.Select(r => r.OutreachId).Distinct());
        Assert.Equal(1, placements);
    }

    [Fact]
    public async Task ReusingClientRequestId_ForDifferentRequest_IsRejected()
    {
        var (service, _) = CreateService();
        service.VoicePlacer = _ => Task.FromResult<string?>("context-1");

        await service.StartAsync(VoiceRequest("case-1042"));

        await Assert.ThrowsAsync<OutreachConflictException>(
            () => service.StartAsync(VoiceRequest("case-1042", phone: "+4599999999")));
    }

    [Fact]
    public async Task RedeliveredInboundSms_IsAppendedOnlyOnce()
    {
        var (service, _) = CreateService(new() { ["Outreach:AutoReply"] = "false" });

        await service.HandleInboundSmsAsync("+4512345678", "Ja tak", "event-1");
        await service.HandleInboundSmsAsync("+4512345678", "Ja tak", "event-1");

        var customer = Assert.Single(await service.ListCustomersAsync());
        var outreach = Assert.Single(await service.GetCustomerTimelineAsync(customer.CustomerId));
        Assert.Equal(1, outreach.Interactions.Count(i => i.Direction == "inbound"));
    }

    [Fact]
    public async Task InboundEmail_ThreadsOnSubjectReference_NotTheMostRecentCase()
    {
        var (service, store) = CreateService(new() { ["Outreach:AutoReply"] = "false" });

        var olderCase = new OutreachRecord
        {
            CustomerId = "kunde-1042",
            Email = "mette@example.dk",
            Channel = "email",
            Status = "awaiting_reply",
            CreatedUtc = DateTimeOffset.UtcNow.AddHours(-2)
        };
        var newerCase = new OutreachRecord
        {
            CustomerId = "kunde-1042",
            Email = "mette@example.dk",
            Channel = "email",
            Status = "awaiting_reply"
        };
        await store.UpsertAsync(olderCase);
        await store.UpsertAsync(newerCase);

        var threaded = await service.TryHandleInboundEmailAsync(
            "Mette@Example.dk",
            $"Re: Norlys [sag: {olderCase.Id[..8]}]",
            "Ja, adressen er korrekt.",
            "message-1");

        Assert.True(threaded);
        var older = await service.GetResultAsync(olderCase.Id);
        var newer = await service.GetResultAsync(newerCase.Id);
        Assert.Contains(older!.Interactions, i => i.Direction == "inbound");
        Assert.DoesNotContain(newer!.Interactions, i => i.Direction == "inbound");
    }

    [Fact]
    public async Task InboundEmail_DropsQuotedHistoryFromTheReply()
    {
        var (service, store) = CreateService(new() { ["Outreach:AutoReply"] = "false" });
        var record = new OutreachRecord
        {
            CustomerId = "kunde-1042",
            Email = "mette@example.dk",
            Channel = "email",
            Status = "awaiting_reply"
        };
        await store.UpsertAsync(record);

        await service.TryHandleInboundEmailAsync(
            "mette@example.dk",
            "Re: Norlys",
            "Ja, det passer.\n\nFrom: Norlys Kundeservice\nSent: 3 August 2026\n\nHej Mette, kan du bekraefte adressen?",
            "message-1");

        var result = await service.GetResultAsync(record.Id);
        var inbound = Assert.Single(result!.Interactions, i => i.Direction == "inbound");
        Assert.Contains("Ja, det passer.", inbound.Text);
        Assert.DoesNotContain("kan du bekraefte adressen", inbound.Text);
    }

    [Theory]
    [InlineData("http://example.com/hook")]          // not HTTPS
    [InlineData("https://127.0.0.1/hook")]           // loopback
    [InlineData("https://169.254.169.254/hook")]     // instance metadata
    public async Task CallbackToUnsafeTarget_IsDeadLetteredWithoutRetrying(string callbackUrl)
    {
        var (service, _) = CreateService();

        // No VoicePlacer is registered, so the outreach fails immediately and becomes terminal.
        var started = await service.StartAsync(new OutreachRequest(
            new OutreachCustomer("kunde-1042", "Mette", "+4512345678", null),
            "voice", CallbackUrl: callbackUrl));
        Assert.Equal("failed", started.Status);

        await service.DispatchDueCallbacksAsync();

        var result = await service.GetResultAsync(started.OutreachId);
        Assert.Equal("dead_lettered", result!.Callback.Status);
        Assert.Equal(1, result.Callback.Attempts);
        Assert.NotNull(result.Callback.LastError);
    }

    [Fact]
    public async Task OutreachWithoutCallbackUrl_ReportsNotRequested()
    {
        var (service, _) = CreateService();
        service.VoicePlacer = _ => Task.FromResult<string?>("context-1");

        var started = await service.StartAsync(VoiceRequest("case-1042"));

        Assert.Equal("not_requested", started.Callback.Status);
        Assert.Equal(0, await service.DispatchDueCallbacksAsync());
    }
}
