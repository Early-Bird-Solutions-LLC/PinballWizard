namespace PinballWizard.Application.Ai.Refusal;

// Contract for loading the curated community_resources.v1.json seed at startup.
//
// The loader is a singleton that reads once from disk on first call and
// caches the result in memory. LoadAsync is called from the DI container
// either eagerly at startup or lazily on first refusal — the service is
// best-effort: a failure throws (fail-fast, not silent miss) so the
// operator discovers a broken seed file at deployment time rather than
// silently serving empty recovery payloads to users.
//
// Per ADR-0026 § 5/6 and feedback_destination_plurality.md, per-category
// minimums are enforced at LOAD time:
//   marketplace     ≥ 3 entries
//   machine_reference ≥ 2 entries
//
// Any seed edit that violates those minimums will throw on startup — the
// fail-fast guard is the primary silent-edit protection for the plurality
// invariant.
public interface ICommunityResourceLoader
{
    // Returns the full curated resource list, sorted alphabetically by
    // name within each category. Throws on first load if the seed file
    // is missing, malformed, or violates plurality minimums. Subsequent
    // calls return the cached in-memory list.
    Task<IReadOnlyList<CommunityResource>> LoadAsync(CancellationToken cancellationToken);

    // Returns resources for the given category, sorted alphabetically.
    // Convenience over filtering the LoadAsync result — callers in
    // RefusalRecoveryService use this to avoid repeated LINQ scans.
    Task<IReadOnlyList<CommunityResource>> LoadByCategoryAsync(
        CommunityResourceCategory category,
        CancellationToken cancellationToken);
}

// Canonical category values that the loader validates against.
// Adding a new value here requires a corresponding entry in the seed file
// (or the loader will fail if the seed references an unknown string).
public enum CommunityResourceCategory
{
    Marketplace = 0,
    MachineReference = 1,
    NewsAndCulture = 2,
    Forums = 3,
    TournamentAndPlay = 4,
    ManufacturerPages = 5,
}
