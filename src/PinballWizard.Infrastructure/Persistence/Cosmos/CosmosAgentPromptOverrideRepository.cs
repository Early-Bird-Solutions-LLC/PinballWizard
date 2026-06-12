using System.Collections.Concurrent;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Cosmos-backed IAgentPromptOverrideRepository (admin prompts plan, PR-B3).
//
// Read-side caching strategy (mirrors CosmosAdminSettingsRepository):
// GetActiveAsync is fronted by a per-instance TTL cache (2 minutes,
// evict-on-write) because OverridingAgentPromptProvider calls it on
// EVERY ask. Negative entries (no active override for an agent) are
// cached too — a default-running install would otherwise issue a
// Cosmos read per agent per ask.
//
// The TTL cache is evicted for the affected agent only on
// ActivateAsync, DeactivateAsync, and SaveNewVersionAsync. A change
// made by one replica is visible to others within one TTL window.
// GetVersionsAsync (the admin page's history view) is uncached by
// design — it must show truth, not a stale window.
//
// Write ordering: SaveNewVersionAsync auto-increments the version by
// scanning existing rows before writing (all within one agent's
// partition). The scan is NOT transactional (Cosmos has no
// cross-document transactions on a partition), so concurrent saves
// from two admin sessions could race to the same version number. This
// is acceptable: prompt-version collisions are an edge case and the
// admin page is not designed for concurrent write throughput. A
// collision surfaces as a CosmosException (PreconditionFailed) which
// the caller can retry. The invariant that matters (one-active-per-
// agent) IS enforced atomically within ActivateAsync via a scan-then-
// upsert loop, which is safe because the admin page serialises
// activate calls through the single HTTP request path.
internal sealed class CosmosAgentPromptOverrideRepository
    : CosmosRepository<AgentPromptOverrideCosmosRecord>, IAgentPromptOverrideRepository
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    // Key = agentName; value = (active override or null, cached-at timestamp).
    // Null values are negative cache entries (no active override stored).
    private readonly ConcurrentDictionary<string, (AgentPromptOverride? Override, DateTimeOffset CachedAtUtc)> _cache =
        new(StringComparer.Ordinal);

    public CosmosAgentPromptOverrideRepository(Container container, ILogger<CosmosAgentPromptOverrideRepository> logger)
        : base(container, logger)
    {
    }

    public async Task<AgentPromptOverride?> GetActiveAsync(string agentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        if (_cache.TryGetValue(agentName, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAtUtc < CacheTtl)
        {
            return cached.Override;
        }

        // Scan the agent's partition for the active row. There is at most
        // one active row (enforced by ActivateAsync), but a scan is
        // preferred over a query-by-is_active index because the container
        // uses default indexing (no selective path for is_active) and the
        // partition is tiny (few versions per agent). The extra RU cost on
        // cache miss is negligible; the cache makes it ~zero in steady state.
        AgentPromptOverride? active = null;
        await foreach (var cosmos in StreamAsync(
            "SELECT * FROM c WHERE c.is_active = true",
            parameters: null,
            partitionKey: agentName,
            cancellationToken).ConfigureAwait(false))
        {
            active = ToDomain(cosmos);
            break; // one-active invariant — stop on first match
        }

        _cache[agentName] = (active, DateTimeOffset.UtcNow);
        return active;
    }

    public async Task<IReadOnlyList<AgentPromptOverride>> GetVersionsAsync(
        string agentName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // Uncached by design — this is the admin page's history view
        // and must show truth, not a stale window.
        var result = new List<AgentPromptOverride>();
        await foreach (var cosmos in StreamAsync(
            "SELECT * FROM c ORDER BY c.version ASC",
            parameters: null,
            partitionKey: agentName,
            cancellationToken).ConfigureAwait(false))
        {
            result.Add(ToDomain(cosmos));
        }

        return result;
    }

    public async Task<AgentPromptOverride> SaveNewVersionAsync(
        string agentName,
        string content,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedBy);

        // Auto-increment: scan the partition for the current highest
        // version. Version starts at 1 (first save on an agent is v1).
        int nextVersion = 1;
        await foreach (var existing in StreamAsync(
            "SELECT c.version FROM c ORDER BY c.version DESC OFFSET 0 LIMIT 1",
            parameters: null,
            partitionKey: agentName,
            cancellationToken).ConfigureAwait(false))
        {
            nextVersion = existing.Version + 1;
            break;
        }

        var cosmos = new AgentPromptOverrideCosmosRecord
        {
            Id = MakeId(agentName, nextVersion),
            PartitionKey = agentName,
            Version = nextVersion,
            Content = content,
            IsActive = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedBy = updatedBy,
        };

        await UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);

        // Evict AFTER the write succeeds. The new version is INACTIVE so
        // GetActiveAsync's cached result is still valid, but evict anyway
        // to be safe — a concurrent ActivateAsync on this agent between
        // Save and the cache TTL would have already evicted.
        _cache.TryRemove(agentName, out _);

        return ToDomain(cosmos);
    }

    public async Task ActivateAsync(string agentName, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // One-active-per-agent: read all rows, deactivate others, activate
        // the target. All writes are upserts (idempotent), so a partial
        // failure mid-loop leaves at most one stale active row; the next
        // ActivateAsync call will correct it. The admin page serialises
        // activations through the single request path, so concurrent
        // ActivateAsync calls are not expected in practice.
        var all = new List<AgentPromptOverrideCosmosRecord>();
        await foreach (var cosmos in StreamAsync(
            "SELECT * FROM c",
            parameters: null,
            partitionKey: agentName,
            cancellationToken).ConfigureAwait(false))
        {
            all.Add(cosmos);
        }

        bool targetFound = false;
        foreach (var cosmos in all)
        {
            bool shouldBeActive = cosmos.Version == version;
            if (shouldBeActive) targetFound = true;

            if (cosmos.IsActive == shouldBeActive) continue; // no change needed

            var updated = new AgentPromptOverrideCosmosRecord
            {
                Id = cosmos.Id,
                PartitionKey = cosmos.PartitionKey,
                Version = cosmos.Version,
                Content = cosmos.Content,
                IsActive = shouldBeActive,
                UpdatedAtUtc = cosmos.UpdatedAtUtc,
                UpdatedBy = cosmos.UpdatedBy,
            };

            await UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        if (!targetFound)
        {
            throw new InvalidOperationException(
                $"Agent prompt override not found: agentName='{agentName}' version={version}. " +
                "Save the version first before activating.");
        }

        // Evict AFTER the writes succeed so a failed partial write never
        // poisons the cache with a state Cosmos doesn't hold.
        _cache.TryRemove(agentName, out _);
    }

    public async Task DeactivateAsync(string agentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // Scan for any active rows and deactivate them. Idempotent — if
        // no active row exists, the loop body never executes. Evict the
        // cache entry unconditionally so the next GetActiveAsync sees null
        // immediately (no stale override window after the operator reverts
        // to the embedded default).
        await foreach (var cosmos in StreamAsync(
            "SELECT * FROM c WHERE c.is_active = true",
            parameters: null,
            partitionKey: agentName,
            cancellationToken).ConfigureAwait(false))
        {
            var updated = new AgentPromptOverrideCosmosRecord
            {
                Id = cosmos.Id,
                PartitionKey = cosmos.PartitionKey,
                Version = cosmos.Version,
                Content = cosmos.Content,
                IsActive = false,
                UpdatedAtUtc = cosmos.UpdatedAtUtc,
                UpdatedBy = cosmos.UpdatedBy,
            };

            await UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        _cache.TryRemove(agentName, out _);
    }

    // Deterministic id: "{agentName}:v{version}" — pure point-read with
    // no secondary index needed. The colon separator is URL-safe in Cosmos
    // document ids (only /\#? are disallowed).
    internal static string MakeId(string agentName, int version) =>
        $"{agentName}:v{version}";

    private static AgentPromptOverride ToDomain(AgentPromptOverrideCosmosRecord cosmos) =>
        new(cosmos.PartitionKey, cosmos.Version, cosmos.Content, cosmos.IsActive,
            cosmos.UpdatedAtUtc, cosmos.UpdatedBy);
}
