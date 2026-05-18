using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Persistence;

/// <summary>
/// Repository for <see cref="Machine"/> aggregates, partitioned by
/// manufacturer. Per ADR 0007 the manufacturer-keyed partitioning lets
/// each per-manufacturer scraper write within its own partition without
/// cross-partition contention.
/// </summary>
public interface IMachineRepository : IRepository<Machine>
{
    /// <summary>
    /// Look up a machine by its OPDB ID. Convenience over
    /// <see cref="IRepository{T}.GetByIdAsync"/> — callers usually have
    /// the OPDB ID and the manufacturer key in hand and don't want to
    /// pass the same value as both id and partition key.
    /// </summary>
    Task<Machine?> GetByOpdbIdAsync(string opdbId, string manufacturer, CancellationToken cancellationToken);

    /// <summary>
    /// Stream every machine for a given manufacturer. Useful for the
    /// admin UI and for per-manufacturer scrapers that want to enumerate
    /// what they already know about.
    /// </summary>
    IAsyncEnumerable<Machine> StreamByManufacturerAsync(string manufacturer, CancellationToken cancellationToken);

    // Cross-partition case-insensitive title lookup, introduced in
    // Phase 3 Wave 2 PR 5 as the backing store for the
    // getMachineByTitle Foundry function tool (per ADR-0014). Returns
    // 0..N machines whose Title equals the argument under
    // STRINGEQUALS-with-case-insensitive comparison; the function tool
    // typically takes the first match.
    IAsyncEnumerable<Machine> QueryByTitleAsync(string title, CancellationToken cancellationToken);

    // Cross-partition groupId lookup per ADR-0029. Returns all base-
    // machine records sharing the same leading OPDB segment (GroupId),
    // which are the distinct Pro / Premium / LE / Collector editions of
    // a single franchise title. The resolved primary machine is included
    // in the results — callers should filter it out if they only want
    // siblings. Expected cardinality: 1–10 records (ADR-0029 § data
    // observation). Cross-partition is unavoidable here because siblings
    // may span manufacturers (unusual but possible), and the groupId
    // field is not the partition key.
    IAsyncEnumerable<Machine> GetSiblingsByGroupIdAsync(string groupId, CancellationToken cancellationToken);
}