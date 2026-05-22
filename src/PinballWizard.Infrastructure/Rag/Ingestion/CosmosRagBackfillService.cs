using System.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Concrete `IRagBackfillService`. Iterates the `scraped_documents`
// container's full change feed using `ChangeFeedStartFrom.Beginning()`
// (raw stream iterator — no lease checkpoints, no processor machinery)
// and routes each document through the same
// `ICosmosChangeFeedHandler<RagSourceDocument>` wired by the hosted
// service.
//
// Why not the Change Feed Processor?  The v3 processor writes a
// continuation-token lease on first init that resolves to the current
// feed tail, not `WithStartTime`.  Documents written before the first
// processor run are unreachable via that path.  The raw stream iterator
// has no lease store — it always starts where you tell it to.
//
// Idempotency: the handler delegates to `ScrapedDocumentChangeFeedHandler`
// which calls `IRagIngestionPipeline.IngestAsync`, which short-circuits
// on matching `IIndexState` content-hash, so re-runs skip already-indexed
// documents without re-embedding.
//
// Concurrency: documents are processed sequentially. The embedding and
// AI Search upsert calls inside the pipeline already batch internally;
// adding outer parallelism here would compete with the retries/resilience
// the underlying HTTP clients apply and would complicate the progress log.
public sealed class CosmosRagBackfillService : IRagBackfillService
{
    private readonly Container _sourceContainer;
    private readonly ICosmosChangeFeedHandler<RagSourceDocument> _handler;
    private readonly RagIngestionOptions _options;
    private readonly ILogger<CosmosRagBackfillService> _logger;

    public CosmosRagBackfillService(
        Container sourceContainer,
        ICosmosChangeFeedHandler<RagSourceDocument> handler,
        IOptions<RagIngestionOptions> options,
        ILogger<CosmosRagBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(sourceContainer);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _sourceContainer = sourceContainer;
        _handler = handler;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RagBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        int processed = 0, indexed = 0, skipped = 0, failed = 0;

        _logger.LogInformation(
            "RAG backfill starting: source={SourceContainer} database={Database} acceptedTypes={AcceptedTypes}.",
            _sourceContainer.Id,
            _sourceContainer.Database.Id,
            string.Join(",", _options.AcceptedDocumentTypes));

        // Raw change-feed stream iterator — reads every document ever
        // written to the container, starting from the very first change
        // record the service retains. `LatestVersion` mode is required
        // for the v3 stream iterator (AllVersionsAndDeletes requires
        // analytical store or continuous backup).
        var iter = _sourceContainer.GetChangeFeedStreamIterator(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 50 });

        _logger.LogDebug("RAG backfill: iterator created, HasMoreResults={HasMore}.", iter.HasMoreResults);

        while (iter.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var response = await iter.ReadNextAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "RAG backfill: page read status={StatusCode} contentLength={ContentLength}.",
                response.StatusCode,
                response.Content?.Length ?? -1);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "RAG backfill: change-feed page returned {StatusCode}; stopping.",
                    response.StatusCode);
                break;
            }

            if (response.Content is null)
                continue;

            ChangeFeedPage? page;
            try
            {
                page = System.Text.Json.JsonSerializer.Deserialize<ChangeFeedPage>(
                    response.Content,
                    PageJsonOptions);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "RAG backfill: failed to deserialize change-feed page; skipping.");
                continue;
            }

            if (page?.Documents is null || page.Documents.Count == 0)
                continue;

            foreach (var doc in page.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;

                // Surface per-document progress every 10 docs so the
                // operator can see the backfill is moving (PDFs take
                // several seconds each).
                if (processed % 10 == 0)
                {
                    _logger.LogInformation(
                        "RAG backfill progress: processed={Processed} indexed={Indexed} skipped={Skipped} failed={Failed}.",
                        processed, indexed, skipped, failed);
                }

                try
                {
                    var outcome = await _handler.HandleAsync(doc, cancellationToken).ConfigureAwait(false);
                    if (outcome == IngestionOutcome.Indexed)
                        indexed++;
                    else
                        skipped++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(
                        ex,
                        "RAG backfill: handler failed for document={DocumentId}; continuing.",
                        doc.DocumentId);
                }
            }
        }

        sw.Stop();
        var result = new RagBackfillResult(processed, indexed, skipped, failed, sw.Elapsed);

        _logger.LogInformation(
            "RAG backfill complete: processed={Processed} indexed={Indexed} skipped={Skipped} failed={Failed} duration={Duration}.",
            result.Processed, result.Indexed, result.Skipped, result.Failed, result.Duration);

        return result;
    }

    private static readonly System.Text.Json.JsonSerializerOptions PageJsonOptions =
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    // Minimal projection for deserializing the change-feed stream response.
    // `RagSourceDocument` carries explicit `[JsonPropertyName]` attributes
    // so the snake_case Cosmos fields (document_id, machine_id) map correctly
    // even with `PropertyNameCaseInsensitive = true`.
    private sealed class ChangeFeedPage
    {
        [System.Text.Json.Serialization.JsonPropertyName("Documents")]
        public List<RagSourceDocument>? Documents { get; init; }
    }
}
