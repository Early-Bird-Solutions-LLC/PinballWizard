using System.Collections.Concurrent;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Cosmos-backed IAdminSettingsRepository (admin settings plan, PR-B1).
//
// Reads are fronted by a per-instance TTL cache (2 minutes, evict-on-write
// — the pattern proven in Conflux's AppConfigurationService) because
// IRuntimeSettings consults settings on EVERY ask: without the cache each
// answer would cost three point reads; with it, steady-state reads are
// dictionary lookups and Cosmos sees at most one read per key per window.
// Absent keys are cached too (negative entries) — most installs run on
// defaults, and an uncached miss would defeat the cache exactly where it
// matters most.
//
// Consequence, documented for the settings page: a change made by one
// replica is visible to others within one TTL window. The page reads
// through GetAllAsync (uncached) so admins always see truth.
internal sealed class CosmosAdminSettingsRepository
    : CosmosRepository<AdminSettingCosmosRecord>, IAdminSettingsRepository
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (AdminSettingRecord? Record, DateTimeOffset CachedAtUtc)> _cache =
        new(StringComparer.Ordinal);

    public CosmosAdminSettingsRepository(Container container, ILogger<CosmosAdminSettingsRepository> logger)
        : base(container, logger)
    {
    }

    public async Task<AdminSettingRecord?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_cache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAtUtc < CacheTtl)
        {
            return cached.Record;
        }

        var cosmos = await GetByIdAsync(key, key, cancellationToken).ConfigureAwait(false);
        var record = cosmos is null ? null : ToDomain(cosmos);

        _cache[key] = (record, DateTimeOffset.UtcNow);
        return record;
    }

    public async Task<IReadOnlyList<AdminSettingRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        // Uncached by design — this is the settings page's load path and
        // must show truth, not a stale window. Tens of documents at most.
        var result = new List<AdminSettingRecord>();

        await foreach (var cosmos in StreamCrossPartitionAsync(
            "SELECT * FROM c",
            parameters: null,
            cancellationToken).ConfigureAwait(false))
        {
            result.Add(ToDomain(cosmos));
        }

        return result;
    }

    public async Task SetAsync(string key, string value, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedBy);

        var cosmos = new AdminSettingCosmosRecord
        {
            Id = key,
            PartitionKey = key,
            Value = value,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedBy = updatedBy,
        };

        await UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);

        // Evict AFTER the write succeeds so a failed upsert never poisons
        // readers with a value Cosmos doesn't hold.
        _cache.TryRemove(key, out _);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            await base.DeleteAsync(key, key, cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Deleting an absent override is a no-op per the contract —
            // "revert to default" is idempotent.
        }

        _cache.TryRemove(key, out _);
    }

    private static AdminSettingRecord ToDomain(AdminSettingCosmosRecord cosmos) =>
        new(cosmos.PartitionKey, cosmos.Value, cosmos.UpdatedAtUtc, cosmos.UpdatedBy);
}
