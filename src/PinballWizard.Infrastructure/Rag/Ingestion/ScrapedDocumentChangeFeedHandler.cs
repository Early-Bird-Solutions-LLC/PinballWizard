using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Concrete `ICosmosChangeFeedHandler<RagSourceDocument>` for the
// W3-2 RAG ingestion worker. Bridges the change-feed payload onto
// `IRagIngestionPipeline.IngestAsync`:
//
//   1. Maps `RagSourceDocument` → `ScrapedDocumentChange` (trims
//      Cosmos system fields the Application layer doesn't need).
//   2. Short-circuits on document type BEFORE fetching bytes — avoids
//      downloading non-indexable binary blobs (firmware, software
//      releases) that would have been filtered by the pipeline anyway.
//   3. Pulls PDF bytes via `IDocumentBytesSource` (default impl
//      `HttpDocumentBytesSource`).
//   4. Invokes the pipeline. Outcome is logged but NOT thrown —
//      `Indexed`, `Skipped_*`, and `DeadLettered` are all valid
//      pipeline returns; only unexpected exceptions bubble up to
//      the hosted service for dead-lettering.
//
// Document-type parsing falls back to `DocumentType.Other` when the
// source string doesn't match a `DocumentType` enum member. Defensive
// rather than throwing because a future schema addition (e.g., a new
// document type the Phase 1 scraper starts emitting) shouldn't
// dead-letter every change until the worker is rebuilt.
public sealed class ScrapedDocumentChangeFeedHandler
    : ICosmosChangeFeedHandler<RagSourceDocument>
{
    private readonly IRagIngestionPipeline _pipeline;
    private readonly IDocumentBytesSource _bytesSource;
    private readonly HashSet<DocumentType> _acceptedTypes;
    private readonly ILogger<ScrapedDocumentChangeFeedHandler> _logger;

    public ScrapedDocumentChangeFeedHandler(
        IRagIngestionPipeline pipeline,
        IDocumentBytesSource bytesSource,
        IOptions<RagIngestionOptions> options,
        ILogger<ScrapedDocumentChangeFeedHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(bytesSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _pipeline = pipeline;
        _bytesSource = bytesSource;
        _acceptedTypes = [.. options.Value.AcceptedDocumentTypes];
        _logger = logger;
    }

    public async Task<IngestionOutcome?> HandleAsync(RagSourceDocument change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        var documentType = ParseDocumentType(change.DocumentType);

        // Short-circuit before fetching bytes — non-accepted types (e.g.
        // firmware blobs, software releases) must not trigger an HTTP
        // download. The pipeline would reject them anyway, but fetching
        // first risks an indefinite stall on large binary responses with
        // no per-stream read timeout (AB#259 backfill hang root cause).
        if (!_acceptedTypes.Contains(documentType))
        {
            _logger.LogDebug(
                "RAG change-feed handler: skipping document={DocumentId} type={DocumentType} (not in accepted set — no download).",
                change.DocumentId, documentType);
            return IngestionOutcome.Skipped_DocumentTypeFiltered;
        }

        var pipelineChange = new ScrapedDocumentChange(
            DocumentId: change.DocumentId,
            DocumentUrl: change.DocumentUrl,
            MachineId: change.MachineId,
            MachineTitle: change.MachineTitle,
            Manufacturer: change.Manufacturer,
            DocumentType: documentType,
            ContentHash: change.ContentHash,
            // Thread LastDownloadedAt from the Cosmos change-feed payload
            // so the indexer can populate the last_scraped_utc AI Search
            // field (PR-C3). Null when the source document is legacy (pre-
            // PR-C3 scraper writes that didn't capture the field) — the
            // indexer propagates null gracefully.
            LastScrapedUtc: change.LastDownloadedAt,
            Edition: change.Edition,
            EditionScope: change.EditionScope);

        await using var pdfStream = await _bytesSource
            .OpenAsync(change.DocumentUrl, cancellationToken).ConfigureAwait(false);

        var outcome = await _pipeline
            .IngestAsync(pipelineChange, pdfStream, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "RAG Change Feed handler: document={DocumentId} machine={MachineId} outcome={Outcome}.",
            change.DocumentId, change.MachineId, outcome);

        return outcome;
    }

    internal static DocumentType ParseDocumentType(string raw) =>
        Enum.TryParse<DocumentType>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : DocumentType.Other;
}
