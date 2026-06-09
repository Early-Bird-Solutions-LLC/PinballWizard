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
// Concurrency: `BackfillConcurrency` documents are processed in parallel
// (default 4). All downstream calls are internal Azure services — no
// politeness throttle applies. Each document already fans out internally
// (EmbeddingMaxConcurrency + IndexUploadConcurrency); this multiplies
// that fan-out. A `SemaphoreSlim` gate caps the outer parallelism so the
// change-feed page loop stays streaming rather than materialising the
// entire corpus into memory.
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
        using var gate = new SemaphoreSlim(_options.BackfillConcurrency);

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
                // LogError (not Warning): a non-2xx response means the run is
                // incomplete — the completion log's processed/indexed counts will
                // appear to succeed but the corpus was only partially walked.
                // Operator must re-run; Warning severity would be too easy to miss.
                _logger.LogError(
                    "RAG backfill: change-feed page returned {StatusCode} after processing {Processed} documents. " +
                    "Backfill is incomplete — re-run required.",
                    response.StatusCode, processed);
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
                // LogError (not Warning): documents on this page are silently
                // dropped — the backfill result will under-count. A schema
                // mismatch in RagSourceDocument would affect every page that
                // contains such a document, making this a potentially systemic
                // data loss. Increment failed by 1 as a sentinel so the result
                // reflects that something was lost (exact page count unknown here).
                Interlocked.Increment(ref failed);
                _logger.LogError(
                    ex,
                    "RAG backfill: failed to deserialize change-feed page after {Processed} documents processed. " +
                    "Documents on this page are NOT counted — possible schema mismatch in RagSourceDocument.",
                    processed);
                continue;
            }

            if (page?.Documents is null || page.Documents.Count == 0)
                continue;

            var docTasks = page.Documents.Select(async doc =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var outcome = await _handler.HandleAsync(doc, cancellationToken).ConfigureAwait(false);
                    int localProcessed, localIndexed, localSkipped, localFailed;
                    if (outcome == IngestionOutcome.Indexed)
                    {
                        localProcessed = Interlocked.Increment(ref processed);
                        localIndexed = Interlocked.Increment(ref indexed);
                        localSkipped = Volatile.Read(ref skipped);
                        localFailed = Volatile.Read(ref failed);
                    }
                    else
                    {
                        localProcessed = Interlocked.Increment(ref processed);
                        localSkipped = Interlocked.Increment(ref skipped);
                        localIndexed = Volatile.Read(ref indexed);
                        localFailed = Volatile.Read(ref failed);
                    }

                    if (localProcessed % 10 == 0)
                    {
                        _logger.LogInformation(
                            "RAG backfill progress: processed={Processed} indexed={Indexed} skipped={Skipped} failed={Failed}.",
                            localProcessed, localIndexed, localSkipped, localFailed);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref processed);
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(
                        ex,
                        "RAG backfill: handler failed for document={DocumentId}; continuing.",
                        doc.DocumentId);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(docTasks).ConfigureAwait(false);
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
