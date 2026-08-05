using Azure.Core;
using Microsoft.Azure.Cosmos;
using System.Collections.Concurrent;

namespace CallerAgent.Outreach;

/// <summary>
/// Persistence for outreach records. Uses Cosmos DB when configured
/// (Cosmos:Endpoint), otherwise falls back to an in-memory store so local dev and
/// the demo keep working before Cosmos is provisioned. Container is partitioned by
/// /customerId; interactions are embedded on the record.
/// </summary>
public sealed class OutreachStore
{
    private Container? _container;
    // Kept even while Cosmos is unreachable so the store can recover without a restart.
    private readonly Container? _configured;
    private DateTimeOffset _nextProbeUtc = DateTimeOffset.MinValue;
    private readonly ILogger<OutreachStore> _logger;
    private readonly ConcurrentDictionary<string, OutreachRecord> _memory = new();

    public bool UsesCosmos => _container is not null;

    public OutreachStore(IConfiguration config, TokenCredential credential, ILogger<OutreachStore> logger)
    {
        _logger = logger;

        var endpoint = config["Cosmos:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning("Cosmos:Endpoint not configured — OutreachStore is using in-memory storage (data is not durable).");
            return;
        }

        var databaseName = config["Cosmos:Database"] ?? "outreach";
        var containerName = config["Cosmos:Container"] ?? "outreach";

        // Singleton CosmosClient (this store is a singleton). AAD auth via managed identity.
        var client = new CosmosClient(endpoint, credential, new CosmosClientOptions
        {
            ApplicationName = "caller-agent",
            ConnectionMode = ConnectionMode.Direct
        });
        _container = client.GetContainer(databaseName, containerName);
        _configured = _container;

        // A tenant Azure Policy may disable public network access on Cosmos, which 403s every
        // request from the Container App's public egress. Probe once at startup; if Cosmos is
        // unreachable, fall back to the in-memory store so the demo keeps working end-to-end.
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _container.ReadContainerAsync(cancellationToken: probeCts.Token).GetAwaiter().GetResult();
            _logger.LogInformation("OutreachStore using Cosmos DB {Database}/{Container}", databaseName, containerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cosmos unreachable — falling back to in-memory OutreachStore (data not durable).");
            _container = null;
            _nextProbeUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        }
    }

    /// <summary>
    /// Re-attach to Cosmos once it becomes reachable again (e.g. public network access was turned
    /// back on), so durability is restored without waiting for a container restart.
    /// </summary>
    private void TryRecoverCosmos()
    {
        if (_container is not null || _configured is null || DateTimeOffset.UtcNow < _nextProbeUtc) return;
        _nextProbeUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _configured.ReadContainerAsync(cancellationToken: cts.Token).GetAwaiter().GetResult();
            _container = _configured;
            _logger.LogInformation("Cosmos reachable again — OutreachStore is durable. Migrating {Count} in-memory records.", _memory.Count);
            foreach (var record in _memory.Values.ToList())
            {
                try
                {
                    _container.UpsertItemAsync(record, new PartitionKey(record.CustomerId)).GetAwaiter().GetResult();
                    _memory.TryRemove(record.Id, out _);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not migrate outreach {Id} to Cosmos", record.Id); }
            }
        }
        catch
        {
            // Still unreachable — stay on the in-memory store and try again after the interval.
        }
    }

    public async Task UpsertAsync(OutreachRecord record)
    {
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        TryRecoverCosmos();
        if (_container is null)
        {
            _memory[record.Id] = record;
            return;
        }
        try
        {
            await _container.UpsertItemAsync(record, new PartitionKey(record.CustomerId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cosmos upsert failed for outreach {Id}", record.Id);
            _memory[record.Id] = record; // keep the demo alive even on a transient Cosmos error
        }
    }

    public async Task<OutreachRecord?> FindByIdAsync(string id)
    {
        if (_container is null)
            return _memory.TryGetValue(id, out var r) ? r : null;

        var q = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", id);
        return await QuerySingleAsync(q);
    }

    public async Task<OutreachRecord?> FindByClientRequestIdAsync(string clientRequestId)
    {
        if (_container is null)
            return _memory.Values.FirstOrDefault(r => r.ClientRequestId == clientRequestId);

        var q = new QueryDefinition("SELECT * FROM c WHERE c.clientRequestId = @requestId")
            .WithParameter("@requestId", clientRequestId);
        return await QuerySingleAsync(q);
    }

    public async Task<OutreachRecord?> FindByEmailConversationIdAsync(string conversationId)
    {
        if (_container is null)
            return _memory.Values.FirstOrDefault(r => r.EmailConversationId == conversationId);

        var q = new QueryDefinition("SELECT * FROM c WHERE c.emailConversationId = @conversationId")
            .WithParameter("@conversationId", conversationId);
        return await QuerySingleAsync(q);
    }

    /// <summary>Resolve the short reference carried in outbound email subjects back to its outreach.</summary>
    public async Task<OutreachRecord?> FindByIdPrefixAsync(string prefix)
    {
        if (_container is null)
            return _memory.Values.FirstOrDefault(r => r.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        var q = new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, @prefix, true)")
            .WithParameter("@prefix", prefix);
        return await QuerySingleAsync(q);
    }

    public async Task<OutreachRecord?> FindByContextIdAsync(string contextId)
    {
        if (_container is null)
            return _memory.Values.FirstOrDefault(r => r.ContextId == contextId);

        var q = new QueryDefinition("SELECT * FROM c WHERE c.contextId = @ctx").WithParameter("@ctx", contextId);
        return await QuerySingleAsync(q);
    }

    /// <summary>Find the most recent outreach awaiting a reply on a given address (phone or email).</summary>
    public async Task<OutreachRecord?> FindLatestAwaitingReplyAsync(string address)
    {
        if (_container is null)
        {
            return _memory.Values
                .Where(r => r.Status == "awaiting_reply" && (r.Phone == address || r.Email == address))
                .OrderByDescending(r => r.CreatedUtc)
                .FirstOrDefault();
        }

        var q = new QueryDefinition(
            "SELECT * FROM c WHERE c.status = 'awaiting_reply' AND (c.phone = @a OR c.email = @a) ORDER BY c.createdUtc DESC")
            .WithParameter("@a", address);
        return await QuerySingleAsync(q);
    }

    /// <summary>
    /// Find the thread an inbound message belongs to: one still awaiting a reply if there is one,
    /// otherwise the most recent thread for that address — customers often write again after a case
    /// was closed, and that should continue the conversation rather than start an orphan.
    /// </summary>
    public async Task<OutreachRecord?> FindLatestForAddressAsync(string address)
    {
        if (_container is null)
        {
            return _memory.Values
                .Where(r => r.Phone == address || r.Email == address)
                .OrderByDescending(r => r.Status == "awaiting_reply")
                .ThenByDescending(r => r.CreatedUtc)
                .FirstOrDefault();
        }

        var q = new QueryDefinition(
            "SELECT * FROM c WHERE (c.phone = @a OR c.email = @a) ORDER BY c.createdUtc DESC")
            .WithParameter("@a", address);
        var recent = await QueryListAsync(q, new QueryRequestOptions { MaxItemCount = 10 }, 10);
        return recent.FirstOrDefault(r => r.Status == "awaiting_reply") ?? recent.FirstOrDefault();
    }

    public async Task<IReadOnlyList<OutreachRecord>> ListByCustomerAsync(string customerId)
    {
        if (_container is null)
        {
            return _memory.Values.Where(r => r.CustomerId == customerId)
                .OrderBy(r => r.CreatedUtc).ToList();
        }

        var q = new QueryDefinition("SELECT * FROM c WHERE c.customerId = @cid ORDER BY c.createdUtc")
            .WithParameter("@cid", customerId);
        return await QueryListAsync(q, new QueryRequestOptions { PartitionKey = new PartitionKey(customerId) });
    }

    public async Task<IReadOnlyList<OutreachRecord>> ListRecentAsync(int limit = 200)
    {
        if (_container is null)
            return _memory.Values.OrderByDescending(r => r.CreatedUtc).Take(limit).ToList();

        var q = new QueryDefinition("SELECT * FROM c ORDER BY c.createdUtc DESC");
        return await QueryListAsync(q, new QueryRequestOptions { MaxItemCount = limit }, limit);
    }

    /// <summary>
    /// Every outreach still awaiting callback delivery. The due-time comparison is deliberately left
    /// to the caller: persisted timestamps are ISO strings whose format differs from a query
    /// parameter's, so comparing them inside Cosmos is not reliable.
    /// </summary>
    public async Task<IReadOnlyList<OutreachRecord>> ListPendingCallbacksAsync(int limit = 100)
    {
        if (_container is null)
            return _memory.Values.Where(record => record.CallbackStatus == "pending").Take(limit).ToList();

        var q = new QueryDefinition("SELECT * FROM c WHERE c.callbackStatus = 'pending'");
        return await QueryListAsync(q, new QueryRequestOptions { MaxItemCount = limit }, limit);
    }

    /// <summary>Wipe every outreach record. Demo/reset aid — disable with Outreach:AllowDataReset=false.</summary>
    public async Task<int> DeleteAllAsync()
    {
        var memoryCount = _memory.Count;
        _memory.Clear();
        if (_container is null) return memoryCount;

        var deleted = 0;
        foreach (var record in await ListRecentAsync(1000))
        {
            try
            {
                await _container.DeleteItemAsync<OutreachRecord>(record.Id, new PartitionKey(record.CustomerId));
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete outreach {Id}", record.Id);
            }
        }
        return deleted + memoryCount;
    }

    private async Task<OutreachRecord?> QuerySingleAsync(QueryDefinition q)
    {
        using var it = _container!.GetItemQueryIterator<OutreachRecord>(q, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        if (it.HasMoreResults)
        {
            foreach (var r in await it.ReadNextAsync()) return r;
        }
        return null;
    }

    private async Task<IReadOnlyList<OutreachRecord>> QueryListAsync(QueryDefinition q, QueryRequestOptions options, int? limit = null)
    {
        var results = new List<OutreachRecord>();
        using var it = _container!.GetItemQueryIterator<OutreachRecord>(q, requestOptions: options);
        while (it.HasMoreResults)
        {
            foreach (var r in await it.ReadNextAsync())
            {
                results.Add(r);
                if (limit is not null && results.Count >= limit) return results;
            }
        }
        return results;
    }
}
