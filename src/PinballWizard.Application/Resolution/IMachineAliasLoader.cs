namespace PinballWizard.Application.Resolution;

// Contract for loading the curated machine_aliases.v1.json seed at startup.
//
// The loader is a singleton that reads once from disk on first call and
// caches the result in memory. LoadAsync throws (fail-fast) on schema
// violations or dangling references so mis-edits are caught at deployment
// time, not silently at resolution time.
//
// Every loaded alias has exactly one of OpdbGroupId or MachineId set, a
// non-empty alias, and a non-empty manufacturerKey. The loader also
// verifies each target exists in the catalog via IMachineAliasCatalog;
// a dangling alias is rejected — it does not mis-attribute anything, but
// it silently does nothing (same failure class as #758).
public interface IMachineAliasLoader
{
    // Returns the full list of curated machine aliases. Throws on first
    // load if the seed file is missing, malformed, or contains invalid
    // entries. Subsequent calls return the cached in-memory list.
    Task<IReadOnlyList<MachineAliasEntry>> LoadAsync(CancellationToken cancellationToken);
}
