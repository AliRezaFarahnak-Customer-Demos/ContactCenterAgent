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
    private readonly Container? _container;
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
        _logger.LogInformation("OutreachStore using Cosmos DB {Database}/{Container}", databaseName, containerName);
    }

    public async Task UpsertAsync(OutreachRecord record)
    {
        record.UpdatedUtc = DateTimeOffset.UtcNow;
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
