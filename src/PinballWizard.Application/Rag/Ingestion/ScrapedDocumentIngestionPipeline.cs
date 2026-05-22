using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Rag.Ingestion;

// Default `IRagIngestionPipeline` implementation per W3-2 design
// (build-spec § Phase 4 item 18). Pure orchestration: every dependency
// is an Application abstraction; no Infrastructure types reach this
// class. The filter branches up front (document type, hash short-circuit)
// avoid expensive work for documents that wouldn't land in the index
// anyway.
//
// Failure posture: a per-document exception inside the pipeline does
// NOT bubble up to the caller. The hosted service's wrapper catches
// and dead-letters the document so the Change Feed batch advances
// (poison-pill resilience). This class throws only for callers'
// explicit cancellation (OperationCanceledException) — the hosted
// service propagates that to abort the batch cleanly.
//
// Telemetry hooks (`pinwiz.rag.changefeed_*`) are deliberately NOT
// emitted from this class. They land in the W3-2 observability PR
// alongside the hosted service which also emits batch-level metrics
// (lease lag, batch duration). Per the gap-closure rule
// (memory/project_observability_followup_per_tool_metrics.md) the
// instruments ship with their emission, not as a documented-but-
// unshipped half — keeping orchestration metric-free here keeps
// that contract clean.
public sealed class ScrapedDocumentIngestionPipeline : IRagIngestionPipeline
{
    private readonly IDocumentTextExtractor _extractor;
    private readonly IChunker _chunker;
    private readonly IRagIndexer _indexer;
    private readonly IIndexState _indexState;
    private readonly RagIndexerOptions _indexerOptions;
    private readonly HashSet<Core.Models.DocumentType> _acceptedTypes;
    private readonly ILogger<ScrapedDocumentIngestionPipeline> _logger;

    public ScrapedDocumentIngestionPipeline(
        IDocumentTextExtractor extractor,
        IChunker chunker,
        IRagIndexer indexer,
        IIndexState indexState,
        IOptions<RagIngestionOptions> options,
        IOptions<RagIndexerOptions> indexerOptions,
        ILogger<ScrapedDocumentIngestionPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(indexState);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(indexerOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _extractor = extractor;
        _chunker = chunker;
        _indexer = indexer;
        _indexState = indexState;
        _indexerOptions = indexerOptions.Value;
        _logger = logger;

        _acceptedTypes = [.. options.Value.AcceptedDocumentTypes];
    }

    public async Task<IngestionOutcome> IngestAsync(
        ScrapedDocumentChange change,
        Stream pdfStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(pdfStream);

        // Filter 1 — document type. Manuals + service bulletins for
        // Phase 4 (per RagIngestionOptions defaults). The metadata-card
        // path is a sibling pipeline.
        if (!_acceptedTypes.Contains(change.DocumentType))
        {
            _logger.LogDebug(
                "RAG ingestion skipped — document {DocumentId} is type {DocumentType}, not in accepted set.",
                change.DocumentId, change.DocumentType);
            return IngestionOutcome.Skipped_DocumentTypeFiltered;
        }

        // Filter 2 — content-hash short-circuit. Avoids re-embedding when
        // a Phase 1 scraper re-polled the source and refreshed metadata
        // without touching the body. Embedding a 200-page manual is the
        // dominant per-doc cost; this is the biggest cost saver on
        // steady-state re-deliveries.
        var lastHash = await _indexState.GetLastIndexedHashAsync(change.DocumentId, cancellationToken)
            .ConfigureAwait(false);
        if (lastHash is not null && string.Equals(lastHash, change.ContentHash, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "RAG ingestion short-circuit — document {DocumentId} hash unchanged ({Hash}).",
                change.DocumentId, change.ContentHash);
            return IngestionOutcome.Skipped_HashUnchanged;
        }

        // Extract.
        var extracted = await _extractor.ExtractAsync(pdfStream, cancellationToken).ConfigureAwait(false);
        if (extracted.Status != ExtractionStatus.Success)
        {
            _logger.LogInformation(
                "RAG ingestion skipped — extraction status {Status} on document {DocumentId} (machine {MachineId}). Error: {Error}",
                extracted.Status, change.DocumentId, change.MachineId, extracted.Error ?? "(none)");
            return IngestionOutcome.Skipped_ExtractionFailed;
        }

        // Chunk.
        var chunkRequest = new ChunkRequest(
            MachineId: change.MachineId,
            MachineTitle: change.MachineTitle,
            Manufacturer: change.Manufacturer,
            DocumentId: change.DocumentId,
            DocumentUrl: change.DocumentUrl,
            DocumentType: change.DocumentType,
            // LastScrapedUtc threaded from the Change Feed payload (PR-C3)
            // so the indexer can populate last_scraped_utc on each chunk.
            LastScrapedUtc: change.LastScrapedUtc);

        var chunks = _chunker.Chunk(extracted, chunkRequest, cancellationToken);
        if (chunks.Count == 0)
        {
            // Defensive: the chunker is supposed to produce at least one
            // chunk for any successfully-extracted document; treat
            // zero-chunks as a soft failure (record state so we don't
            // retry on every re-delivery) and log loudly.
            _logger.LogWarning(
                "RAG ingestion: chunker produced zero chunks for document {DocumentId} (machine {MachineId}). Recording state to prevent retry loop.",
                change.DocumentId, change.MachineId);
            await _indexState.RecordIndexedAsync(
                change.DocumentId, change.ContentHash, chunkCount: 0, failureCount: 0, cancellationToken)
                .ConfigureAwait(false);
            return IngestionOutcome.Indexed;
        }

        // Embed + upsert. AiSearchRagIndexer batches embedding calls
        // and AI Search uploads internally; we treat the result as
        // "happy unless transport-level exception".
        var upsertResult = await _indexer.UpsertAsync(chunkRequest, chunks, _indexerOptions, cancellationToken)
            .ConfigureAwait(false);

        // Record state only when the source document carries a content hash.
        // Seeded documents (from catalog.json) may have an empty ContentHash;
        // without a hash there is no short-circuit value to store, so skip
        // state recording — the document will re-embed on the next backfill
        // run rather than being erroneously dead-lettered.
        // Failures are surfaced (as failureCount on the state row) but DO NOT
        // change the IngestionOutcome — partial failures are common (e.g., one
        // chunk exceeds the AI Search size cap, the rest succeed) and the alarm
        // path is the dead-letter counter from the hosted service when
        // failureCount exceeds MaxFailuresPerDocument.
        if (!string.IsNullOrWhiteSpace(change.ContentHash))
        {
            await _indexState.RecordIndexedAsync(
                change.DocumentId,
                change.ContentHash,
                chunkCount: upsertResult.Indexed,
                failureCount: upsertResult.Failures.Count,
                cancellationToken).ConfigureAwait(false);
        }

        if (upsertResult.Failures.Count > 0)
        {
            _logger.LogInformation(
                "RAG ingestion: document {DocumentId} indexed {Indexed} chunks with {FailureCount} per-chunk failures (machine {MachineId}).",
                change.DocumentId, upsertResult.Indexed, upsertResult.Failures.Count, change.MachineId);
        }
        else
        {
            _logger.LogDebug(
                "RAG ingestion: document {DocumentId} indexed {Indexed} chunks (machine {MachineId}).",
                change.DocumentId, upsertResult.Indexed, change.MachineId);
        }

        return IngestionOutcome.Indexed;
    }
}
