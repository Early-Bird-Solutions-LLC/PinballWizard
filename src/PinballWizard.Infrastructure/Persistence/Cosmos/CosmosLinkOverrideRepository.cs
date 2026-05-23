using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Cosmos-backed ILinkOverrideRepository.
//
// Writes into and reads from the `link_overrides` container.
// Partition key: source_pattern. Document id: source_pattern (identical —
// one override per pattern with deterministic upsert semantics).
//
// LoadAllAsync issues a cross-partition SELECT * to load all overrides for
// startup caching by the linker. In practice < 1,000 records so eager load
// is safe and avoids per-link-resolution latency.
internal sealed class CosmosLinkOverrideRepository
    : CosmosRepository<LinkOverrideCosmosRecord>, ILinkOverrideRepository
{
    public CosmosLinkOverrideRepository(Container container, ILogger<CosmosLinkOverrideRepository> logger)
        : base(container, logger)
    {
    }

    public async Task<IReadOnlyDictionary<string, LinkOverrideRecord>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LinkOverrideRecord>();

        await foreach (var cosmos in StreamAsync(
            "SELECT * FROM c",
            parameters: null,
            partitionKey: null,
            cancellationToken).ConfigureAwait(false))
        {
            var domain = ToDomain(cosmos);
            result[domain.SourcePattern] = domain;
        }

        return result;
    }

    public async Task UpsertAsync(LinkOverrideRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var cosmos = ToCosmos(record);
        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LinkOverrideRecord?> GetAsync(string sourcePattern, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePattern);

        var cosmos = await GetByIdAsync(sourcePattern, sourcePattern, cancellationToken).ConfigureAwait(false);
        return cosmos is null ? null : ToDomain(cosmos);
    }

    public async Task DeleteAsync(string sourcePattern, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePattern);

        await base.DeleteAsync(sourcePattern, sourcePattern, cancellationToken).ConfigureAwait(false);
    }

    private static LinkOverrideCosmosRecord ToCosmos(LinkOverrideRecord record) =>
        new()
        {
            Id = record.SourcePattern,
            PartitionKey = record.SourcePattern,
            MachineIds = record.MachineIds,
            CreatedBy = record.CreatedBy,
            CreatedAt = record.CreatedAt,
            Notes = record.Notes,
        };

    private static LinkOverrideRecord ToDomain(LinkOverrideCosmosRecord cosmos) =>
        new()
        {
            SourcePattern = cosmos.PartitionKey,
            MachineIds = cosmos.MachineIds,
            CreatedBy = cosmos.CreatedBy,
            CreatedAt = cosmos.CreatedAt,
            Notes = cosmos.Notes,
        };
}
