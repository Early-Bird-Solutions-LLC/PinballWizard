using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Cosmos-backed IScrapedDocumentRepository.
//
// Writes into the `scraped_documents` container in the shape that
// `RagSourceDocument` (the change-feed read projection) expects. This is
// the write side of the seeder → Change Feed → RAG ingestion pipeline:
// once a document is upserted here, the RagIngestionWorker's Change Feed
// processor picks it up and drives it through the embedding pipeline.
//
// Partition key: machine_id (OPDB ID).
// Document id:   document_id (SHA-256 of file URL, `doc_` prefix).
internal sealed class CosmosScrapedDocumentRepository
    : CosmosRepository<ScrapedDocumentRecord>, IScrapedDocumentRepository
{
    public CosmosScrapedDocumentRepository(Container container, ILogger<CosmosScrapedDocumentRepository> logger)
        : base(container, logger)
    {
    }

    public async Task UpsertAsync(
        DocumentRecord record,
        string machineId,
        string machineTitle,
        string manufacturer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);

        var cosmos = new ScrapedDocumentRecord
        {
            Id = record.DocumentId,
            PartitionKey = machineId,
            DocumentId = record.DocumentId,
            DocumentUrl = record.Source?.FileUrl ?? string.Empty,
            MachineTitle = machineTitle,
            Manufacturer = manufacturer,
            DocumentType = record.Classification?.DocumentType.ToString() ?? string.Empty,
            ContentHash = record.File?.Sha256 ?? string.Empty,
            LastDownloadedAt = record.Timeline?.LastDownloadedAt is { } lda
                ? new DateTimeOffset(lda, TimeSpan.Zero)
                : null,
            // The catalog-seeder path has no per-document edition resolution;
            // a seeded document applies to its whole machine → franchise-wide.
            EditionScope = ScrapedDocumentRecord.ToWire(EditionScope.FranchiseWide),
        };

        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertFromRawAsync(
        RawDocumentRecord raw,
        string machineId,
        string machineTitle,
        string manufacturer,
        string? edition,
        EditionScope editionScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);

        // Id = "{documentId}_{machineId}" so a single raw record that fans
        // out to multiple machines produces one Cosmos item per machine
        // without collision on the shared document_id.
        var cosmos = new ScrapedDocumentRecord
        {
            Id = $"{raw.DocumentId}_{machineId}",
            PartitionKey = machineId,
            DocumentId = raw.DocumentId,
            DocumentUrl = raw.Source.FileUrl,
            MachineTitle = machineTitle,
            Manufacturer = manufacturer,
            DocumentType = raw.DocumentType.ToString(),
            ContentHash = raw.ContentHash ?? string.Empty,
            LastDownloadedAt = raw.Timeline.LastDownloadedAt is { } lda
                ? new DateTimeOffset(lda, TimeSpan.Zero)
                : null,
            Edition = edition,
            EditionScope = ScrapedDocumentRecord.ToWire(editionScope),
        };

        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> StreamByDocumentIdAsync(
        string documentId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        // Cross-partition by design: fan-out rows for one document_id live in
        // different machine_id partitions. This is an admin/re-link path (not a
        // user-facing query) and returns only the handful of rows for one doc.
        // Projects VALUE c.machine_id (not SELECT *) so we hydrate only the
        // partition key, not the whole document — cheaper RU + payload when this
        // runs once per doc across a full --relink-all.
        var queryDefinition = new QueryDefinition(
            "SELECT VALUE c.machine_id FROM c WHERE c.document_id = @docId")
            .WithParameter("@docId", documentId);

        using var iterator = Container.GetItemQueryIterator<string>(queryDefinition);
        while (iterator.HasMoreResults)
        {
            var page = await ExecuteWithMetricsAsync(
                "query",
                async ct =>
                {
                    var p = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                    return (p, p.RequestCharge);
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var machineId in page)
            {
                if (!string.IsNullOrWhiteSpace(machineId))
                {
                    yield return machineId;
                }
            }
        }
    }

    // IScrapedDocumentRepository.DeleteFanOutRowAsync — deletes the fan-out row
    // "{documentId}_{machineId}" in the machineId partition. Idempotent (the base
    // DeleteAsync treats a missing row as success).
    // NOTE: targets ONLY linker fan-out rows (UpsertFromRawAsync, id =
    // "{documentId}_{machineId}"). It will NOT match a catalog-seeder row
    // (UpsertAsync, id = "{documentId}" with no machine suffix) — by design, since
    // the linker only ever prunes its own fan-out. If the seeder write path is ever
    // revived, unify its id scheme so stale seeder rows are prunable too.
    public Task DeleteFanOutRowAsync(string documentId, string machineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        return DeleteAsync($"{documentId}_{machineId}", machineId, cancellationToken);
    }
}
