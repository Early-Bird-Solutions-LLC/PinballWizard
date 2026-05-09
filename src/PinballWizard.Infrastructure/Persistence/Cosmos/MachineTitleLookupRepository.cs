using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Cosmos-backed <see cref="IMachineTitleLookupRepository"/> per
/// <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 4</see>.
/// Inherits from <see cref="CosmosRepository{T}"/> so every SDK call —
/// the convenience <c>GetByTitleAsync</c> / <c>DeleteByTitleAsync</c>
/// methods plus the inherited <see cref="IRepository{T}.UpsertAsync"/>
/// — routes through <c>ExecuteWithMetricsAsync</c> and emits on
/// <c>pinwiz.cosmos.ru_charge</c> / <c>pinwiz.cosmos.query_duration_ms</c>
/// tagged <c>container=machine_title_lookups</c> per ADR-0025 § 8.
/// </summary>
public sealed class MachineTitleLookupRepository
    : CosmosRepository<MachineTitleLookup>, IMachineTitleLookupRepository
{
    /// <summary>Initializes a new repository wrapping the <c>machine_title_lookups</c> container.</summary>
    public MachineTitleLookupRepository(Container container, ILogger<MachineTitleLookupRepository> logger)
        : base(container, logger)
    {
    }

    /// <inheritdoc />
    public Task<MachineTitleLookup?> GetByTitleAsync(string title, CancellationToken cancellationToken)
    {
        var normalized = MachineTitleLookup.NormalizeTitle(title);
        // id == partition key value, by design — see MachineTitleLookup
        // class remarks. Two equal arguments to GetByIdAsync is the
        // intended contract, not a bug.
        return GetByIdAsync(normalized, normalized, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteByTitleAsync(string title, CancellationToken cancellationToken)
    {
        var normalized = MachineTitleLookup.NormalizeTitle(title);
        return DeleteAsync(normalized, normalized, cancellationToken);
    }
}
