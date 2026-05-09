using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Landing;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Cosmos-backed <see cref="IFeaturedMachineRepository"/> per
/// <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 4</see>.
/// Inherits from <see cref="CosmosRepository{T}"/> so every SDK call —
/// the <c>GetAllAsync</c> / <c>GetAllDocumentsAsync</c> convenience methods
/// plus the inherited <see cref="IRepository{T}.UpsertAsync"/> — routes
/// through <c>ExecuteWithMetricsAsync</c> and emits on
/// <c>pinwiz.cosmos.ru_charge</c> / <c>pinwiz.cosmos.query_duration_ms</c>
/// tagged <c>container=featured_machines</c> per ADR-0025 § 8.
/// </summary>
public sealed class FeaturedMachineRepository
    : CosmosRepository<FeaturedMachineDocument>, IFeaturedMachineRepository
{
    /// <summary>Initializes a new repository wrapping the <c>featured_machines</c> container.</summary>
    public FeaturedMachineRepository(Container container, ILogger<FeaturedMachineRepository> logger)
        : base(container, logger)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeaturedMachine>> GetAllAsync(CancellationToken cancellationToken)
    {
        var documents = await GetAllDocumentsAsync(cancellationToken).ConfigureAwait(false);

        return documents
            .Select(d => new FeaturedMachine(
                MachineId: d.Id,
                Title: d.Title,
                OpdbId: d.OpdbId,
                DisplayOrder: d.DisplayOrder,
                Tagline: d.Tagline))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeaturedMachineDocument>> GetAllDocumentsAsync(CancellationToken cancellationToken)
    {
        // The featured_machines container is a curated set of ~6 documents
        // seeded by --seed-featured-machines. GetAllAsync materialises all
        // documents via a single cross-container query (acceptable here
        // because the container is bounded to ~6 entries and the landing
        // page is the only reader — no fan-out risk). The query is scoped
        // to the container and ordered by display_order so the landing strip
        // renders correctly without a client-side sort.
        //
        // ADR-0025 § 6 note: cross-partition queries are acceptable for
        // bounded read-all patterns on small, write-rarely containers. This
        // container has no partition-key fan-out risk (~6 docs, one slug per
        // doc). Any future growth beyond ~50 entries should revisit and pin
        // slug-by-slug point-reads instead.
        var results = new List<FeaturedMachineDocument>();

        await foreach (var doc in StreamAsync(
            "SELECT * FROM c ORDER BY c.display_order ASC",
            parameters: null,
            partitionKey: null,
            cancellationToken).ConfigureAwait(false))
        {
            results.Add(doc);
        }

        Logger.LogDebug(
            "FeaturedMachineRepository.GetAllDocumentsAsync returned {Count} document(s).",
            results.Count);

        return results.AsReadOnly();
    }
}
