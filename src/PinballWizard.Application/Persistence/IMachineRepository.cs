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
}
