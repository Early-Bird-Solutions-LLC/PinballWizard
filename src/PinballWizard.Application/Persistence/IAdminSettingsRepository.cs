namespace PinballWizard.Application.Persistence;

// Runtime-mutable Wizard configuration (admin settings plan, PR-B1).
//
// Settings layer OVER IOptions, never replace it: a Cosmos row for a
// well-known key overrides the appsettings/env default; deleting the row
// reverts to the default. IRuntimeSettings (Ai/Hosting) is the typed
// read-side facade consumers use — this repository is the raw store the
// /admin/settings page writes through.
//
// Implementations cache reads (the Cosmos one holds a 2-minute TTL cache,
// evicted on write) so per-ask reads cost no RU in steady state. A changed
// setting therefore applies within one TTL window, no restart.
public interface IAdminSettingsRepository
{
    // Point read by well-known key. Null = no override stored (caller
    // falls back to the IOptions default).
    Task<AdminSettingRecord?> GetAsync(string key, CancellationToken cancellationToken);

    // All stored overrides — the /admin/settings page's load path.
    // The container holds tens of documents at most.
    Task<IReadOnlyList<AdminSettingRecord>> GetAllAsync(CancellationToken cancellationToken);

    // Upsert an override. updatedBy is the authenticated admin's name —
    // the audit gap Conflux's equivalent never closed.
    Task SetAsync(string key, string value, string updatedBy, CancellationToken cancellationToken);

    // Remove an override = revert that setting to its IOptions default.
    // Deleting an absent key is a no-op, not an error.
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

// One stored override. id == key == partition key (pure point reads).
public sealed record AdminSettingRecord(
    string Key,
    string Value,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);
