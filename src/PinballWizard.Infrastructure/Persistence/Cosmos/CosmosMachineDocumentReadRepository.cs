using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Tier 1 read repository for the `scraped_documents` container.
//
// Implements IMachineDocumentReadRepository by extending CosmosRepository<ScrapedDocumentReadProjection>
// so every SDK call routes through the base StreamAsync (single-partition, partition key = machine_id)
// rather than calling GetItemQueryIterator directly. This keeps the file out of the ADR-0036
// cross-partition allow-list and ensures per-page RU + duration metrics are emitted automatically.
//
// Reads the narrow ScrapedDocumentReadProjection, not the full write-model ScrapedDocumentRecord:
// the projection carries only the six scraped-side fields this read uses (DocumentId, DocumentType,
// DocumentUrl, Edition, EditionScope, LastDownloadedAt) and declares no `required` fields, so it
// tolerates documents written before later required fields existed (e.g. edition_scope, #318) instead
// of throwing on deserialization. See ScrapedDocumentReadProjection for the rationale.
//
// Enrichment: the raw-side fields (LinkText, LinkStatus, ResolutionStrategy, SizeBytes, PageCount)
// live in the `scraped_documents_raw` container and are fetched per-document via
// IRawDocumentRepository.GetAsync. The raw doc may be null for documents that have not yet been
// processed by the linker — enrichment fields null-propagate in that case.
internal sealed class CosmosMachineDocumentReadRepository
    : CosmosRepository<ScrapedDocumentReadProjection>, IMachineDocumentReadRepository
{
    private readonly IRawDocumentRepository _rawDocs;

    public CosmosMachineDocumentReadRepository(
        Container container,
        IRawDocumentRepository rawDocs,
        ILogger<CosmosRepository<ScrapedDocumentReadProjection>> logger)
        : base(container, logger)
    {
        ArgumentNullException.ThrowIfNull(rawDocs);
        _rawDocs = rawDocs;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<MachineDocumentLink> StreamByMachineIdAsync(
        string machineId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        // Single-partition scan scoped to machine_id (Tier 1 per ADR-0036).
        // Routes through base.StreamAsync — no direct GetItemQueryIterator call.
        // Projects only the columns the read uses (see ScrapedDocumentReadProjection).
        await foreach (var doc in StreamAsync(
            "SELECT c.document_id, c.document_type, c.document_url, c.edition, c.edition_scope, c.last_downloaded_at FROM c",
            parameters: null,
            partitionKey: machineId,
            cancellationToken).ConfigureAwait(false))
        {
            // Fetch enrichment from scraped_documents_raw (may be null if linker hasn't run).
            var raw = await _rawDocs.GetAsync(doc.DocumentId, cancellationToken).ConfigureAwait(false);

            yield return new MachineDocumentLink(
                DocumentId:         doc.DocumentId,
                DocumentType:       doc.DocumentType,
                DocumentUrl:        doc.DocumentUrl,
                LinkText:           raw?.Source.LinkText,
                Edition:            doc.Edition,
                EditionScope:       doc.EditionScope,
                LinkStatus:         raw?.LinkStatus.ToString(),
                ResolutionStrategy: raw?.ResolutionStrategy,
                LastDownloadedUtc:  doc.LastDownloadedAt,
                SizeBytes:          raw?.File?.SizeBytes,
                PageCount:          raw?.File?.PageCount);
        }
    }
}
