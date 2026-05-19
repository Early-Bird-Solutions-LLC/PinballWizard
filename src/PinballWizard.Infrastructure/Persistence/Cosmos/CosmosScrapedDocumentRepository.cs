using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
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
        };

        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
    }
}
