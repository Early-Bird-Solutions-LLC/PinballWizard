using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Persistence;

/// <summary>
/// Repository for the <see cref="MachineTitleLookup"/> materialized view
/// per <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 4</see>.
/// Backs the <c>getMachineByTitle</c> Foundry function tool's point-read
/// path; the cross-partition <c>MachineRepository.QueryByTitleAsync</c>
/// remains as a fallback for the unmigrated-lookup case (logged at
/// warning so operators see the gap).
/// </summary>
public interface IMachineTitleLookupRepository : IRepository<MachineTitleLookup>
{
    /// <summary>
    /// Look up the entry for <paramref name="title"/>. Normalizes the
    /// title via <see cref="MachineTitleLookup.NormalizeTitle"/> and
    /// issues a single point read against the
    /// <c>machine_title_lookups</c> container. Returns <c>null</c> if
    /// no row exists for the normalized title.
    /// </summary>
    Task<MachineTitleLookup?> GetByTitleAsync(string title, CancellationToken cancellationToken);

    /// <summary>
    /// Delete the lookup row for <paramref name="title"/>. Idempotent —
    /// no-op if the row does not exist (matches
    /// <see cref="IRepository{T}.DeleteAsync"/>'s contract).
    /// </summary>
    Task DeleteByTitleAsync(string title, CancellationToken cancellationToken);
}
