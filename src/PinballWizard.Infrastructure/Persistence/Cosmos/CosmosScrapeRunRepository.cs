using System.Runtime.CompilerServices;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Cosmos-backed IScrapeRunRepository. Writes into / reads from the scrape_runs
// container. Partition key = source_id; document id = "{source_id}_{run_at}" (deterministic,
// no Guid/Random). StreamBySourceAsync is a single-partition newest-first read (Tier 1).
internal sealed class CosmosScrapeRunRepository
    : CosmosRepository<ScrapeRunCosmosRecord>, IScrapeRunRepository
{
    public CosmosScrapeRunRepository(Container container, ILogger<CosmosScrapeRunRepository> logger)
        : base(container, logger)
    {
    }

    public async Task WriteAsync(ScrapeRunRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await base.UpsertAsync(ToCosmos(record), cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ScrapeRunRecord> StreamBySourceAsync(
        string sourceId,
        int maxCount,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var parameters = new Dictionary<string, object> { ["maxCount"] = maxCount };
        await foreach (var cosmos in StreamAsync(
            "SELECT TOP @maxCount * FROM c ORDER BY c.run_at DESC",
            parameters,
            partitionKey: sourceId,
            cancellationToken).ConfigureAwait(false))
        {
            yield return ToDomain(cosmos);
        }
    }

    private static string DeriveId(string sourceId, DateTimeOffset runAt) =>
        PinballWizard.Core.Models.ScrapeRunId.For(sourceId, runAt);

    private static ScrapeRunCosmosRecord ToCosmos(ScrapeRunRecord r) => new()
    {
        Id = DeriveId(r.SourceId, r.RunAt),
        PartitionKey = r.SourceId,
        RunAt = r.RunAt,
        DurationSeconds = r.DurationSeconds,
        Succeeded = r.Succeeded,
        DocumentsDiscovered = r.DocumentsDiscovered,
        ErrorMessage = r.ErrorMessage,
    };

    private static ScrapeRunRecord ToDomain(ScrapeRunCosmosRecord c) => new()
    {
        SourceId = c.PartitionKey,
        RunAt = c.RunAt,
        DurationSeconds = c.DurationSeconds,
        Succeeded = c.Succeeded,
        DocumentsDiscovered = c.DocumentsDiscovered,
        ErrorMessage = c.ErrorMessage,
    };
}
