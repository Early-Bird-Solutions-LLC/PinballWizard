using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// Default `IRagIndexer` impl. Embeds chunks via `IChunkEmbedder` then
// upserts into the `pinwiz-rag-v1` AI Search index per ADR-0021.
//
// Idempotency: chunk_id = `chk_` + first 16 hex chars of
//   SHA-256(machine_id ‖ '|' ‖ document_id ‖ '|' ‖ page_start ‖ '-' ‖
//           page_end ‖ '#' ‖ chunk_index)
// Pipe separators prevent boundary collisions (`m1d1` vs `m1` ‖ `d1`).
// 16-hex truncation matches the project's mch_/doc_ deterministic-ID
// convention (CLAUDE.md § Provenance model); collision space at 64 bits
// is comfortable past Phase 4.5 corpus volume.
//
// Concurrency: batches of `BatchSize` chunks are embedded then uploaded
// in parallel under `IndexUploadConcurrency`. The embed step within
// each batch worker is gated by a separate `EmbeddingMaxConcurrency`
// semaphore — TPM is the dominant Azure OpenAI bottleneck and an
// idle index-upload worker shouldn't hold an embed slot.
//
// Failure surfacing: per-document failures (length-exceeded, schema
// validation, etc.) come back inside `IndexDocumentsResult.Results`;
// the indexer projects them into `IndexUpsertFailure[]` so the caller
// (Cosmos Change Feed Function in W3-2) can decide retry / drop /
// alert. Transport-level failures (auth, network, 5xx) propagate as
// the SDK's `RequestFailedException`.
public sealed class AiSearchRagIndexer : IRagIndexer
{
    private readonly SearchClient _searchClient;
    private readonly IChunkEmbedder _embedder;
    private readonly ILogger<AiSearchRagIndexer> _logger;

    public AiSearchRagIndexer(
        SearchClient searchClient,
        IChunkEmbedder embedder,
        ILogger<AiSearchRagIndexer> logger)
    {
        ArgumentNullException.ThrowIfNull(searchClient);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(logger);
        _searchClient = searchClient;
        _embedder = embedder;
        _logger = logger;
    }

    public async Task<IndexUpsertResult> UpsertAsync(
        ChunkRequest request,
        IReadOnlyList<Chunk> chunks,
        RagIndexerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(options);

        if (chunks.Count == 0)
        {
            _logger.LogDebug(
                "RAG index upsert: zero chunks for {DocumentId}; nothing to do.",
                request.DocumentId);
            return new IndexUpsertResult(Indexed: 0, Failures: []);
        }

        ValidateOptions(options);

        // Stopwatch wraps the full UpsertAsync body so the histogram captures
        // user-felt latency including embed-TPM throttling, semaphore waits,
        // and per-batch upload — not just one SDK call. Emitted in `finally`
        // so cancellation + transport failures both surface a duration
        // sample (failures still count as latency the operator paid for).
        var stopwatch = Stopwatch.StartNew();
        var documentTypeTag = new KeyValuePair<string, object?>(
            "document_type",
            request.DocumentType.ToString());

        // Materialize chunk → document mappings up front so the
        // batch-worker code path is purely SDK / I/O, not derivation.
        var documents = new IndexedChunkDocument[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            documents[i] = MapToDocument(request, chunks[i]);
        }

        var batches = BatchIndices(chunks.Count, options.BatchSize);

        using var embedGate = new SemaphoreSlim(options.EmbeddingMaxConcurrency);
        using var uploadGate = new SemaphoreSlim(options.IndexUploadConcurrency);

        // First-failure-cancels-siblings: a transient 401 / 429 / 5xx
        // on one batch should NOT let the remaining 29 batches keep
        // burning embed-TPM + upload calls. Linked CTS gives us
        // structured cancellation; the caller's `cancellationToken`
        // still propagates if it cancels first. Idempotent re-run
        // recovers any work in flight when the failure fired.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workToken = linkedCts.Token;

        var indexedTotal = 0;
        var failures = new List<IndexUpsertFailure>();
        var aggregateLock = new object();

        var batchTasks = batches.Select(async (range) =>
        {
            var (start, count) = range;
            try
            {
                var batchTexts = new string[count];
                for (int i = 0; i < count; i++)
                {
                    batchTexts[i] = chunks[start + i].Text;
                }

                // Embed step (TPM-gated) — release the embed slot before
                // taking the upload slot so the two semaphores can pipeline.
                // The upload range (up to BatchSize=1000) is sub-batched into
                // EmbeddingBatchSize-sized embedding calls: a single huge call
                // (e.g. 140 manual chunks) exceeded the embedding client's ~100s
                // network timeout (AB#259). Sub-batches embed in seconds and are
                // concatenated back in order so the per-range contract holds.
                var vectors = new ReadOnlyMemory<float>[count];
                await embedGate.WaitAsync(workToken).ConfigureAwait(false);
                try
                {
                    foreach (var (subStart, subCount) in BatchIndices(count, options.EmbeddingBatchSize))
                    {
                        var subTexts = new string[subCount];
                        Array.Copy(batchTexts, subStart, subTexts, 0, subCount);

                        var subVectors = await _embedder
                            .EmbedBatchAsync(subTexts, workToken)
                            .ConfigureAwait(false);

                        if (subVectors.Count != subCount)
                        {
                            throw new InvalidOperationException(
                                $"Embedder returned {subVectors.Count} vectors for {subCount} chunks; embedder contract violated.");
                        }

                        for (int i = 0; i < subCount; i++)
                        {
                            vectors[subStart + i] = subVectors[i];
                        }
                    }
                }
                finally
                {
                    embedGate.Release();
                }

                for (int i = 0; i < count; i++)
                {
                    documents[start + i].ContentEmbedding = vectors[i].ToArray();
                }

                // Upload step — `Upload` action replaces by key (idempotent
                // for fixed chunk IDs).
                var batchSlice = new IndexedChunkDocument[count];
                Array.Copy(documents, start, batchSlice, 0, count);
                var batch = IndexDocumentsBatch.Upload<IndexedChunkDocument>(batchSlice);

                await uploadGate.WaitAsync(workToken).ConfigureAwait(false);
                Response<IndexDocumentsResult> response;
                try
                {
                    response = await _searchClient
                        .IndexDocumentsAsync(batch, cancellationToken: workToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    uploadGate.Release();
                }

                int batchIndexed = 0;
                var batchFailures = new List<IndexUpsertFailure>();
                foreach (var result in response.Value.Results)
                {
                    if (result.Succeeded)
                    {
                        batchIndexed++;
                    }
                    else
                    {
                        batchFailures.Add(new IndexUpsertFailure(
                            ChunkId: result.Key,
                            StatusCode: result.Status,
                            ErrorMessage: result.ErrorMessage ?? string.Empty));
                    }
                }

                lock (aggregateLock)
                {
                    indexedTotal += batchIndexed;
                    failures.AddRange(batchFailures);
                }
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // Sibling-batch failure cancelled us — let the
                // originating exception surface from `Task.WhenAll`
                // rather than masking it with this cancellation.
                throw;
            }
            catch (Exception)
            {
                // Cancel every other batch on the first transport-
                // level failure so we don't burn embed-TPM + upload
                // calls finishing 29 more batches whose result will
                // be discarded anyway. Idempotent re-run recovers.
                linkedCts.Cancel();
                throw;
            }
        });

        try
        {
            await Task.WhenAll(batchTasks).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            // Emit duration regardless of success/failure — operators paid
            // for the latency either way and the histogram should reflect
            // both shapes. Tag by document_type so dashboards can compare
            // bulletin-shaped (small, fast) vs. manual-shaped (large,
            // slower) ingest cost on the same axis.
            PinballWizardTelemetry.RagIndexingDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                documentTypeTag);
        }

        // Increment indexed-chunk counter only on success — failures
        // surface as IndexUpsertResult.Failures and are intentionally NOT
        // counted as "indexed" (the whole point of the per-doc result
        // surfacing is to distinguish success volume from attempt volume).
        if (indexedTotal > 0)
        {
            PinballWizardTelemetry.RagIndexedChunks.Add(
                indexedTotal,
                documentTypeTag);
        }

        _logger.LogInformation(
            "RAG index upsert: document={DocumentId} chunks={ChunkCount} indexed={Indexed} failed={Failed} batches={BatchCount} duration={DurationMs:F1}ms",
            request.DocumentId,
            chunks.Count,
            indexedTotal,
            failures.Count,
            batches.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        return new IndexUpsertResult(indexedTotal, failures);
    }

    // Compute the deterministic chunk_id per the contract on
    // `IRagIndexer`. Matches the project's `mch_` / `doc_` pattern:
    // `chk_` + first 16 hex chars of SHA-256(canonical-form). Pipe
    // separators between identifier components prevent boundary
    // collisions (e.g., `mch_a|mch_b` vs `mch_a|mch` ‖ `_b`).
    internal static string ComputeChunkId(
        string machineId,
        string documentId,
        int pageStart,
        int pageEnd,
        int chunkIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{machineId}|{documentId}|{pageStart}-{pageEnd}#{chunkIndex}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"chk_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    internal static IndexedChunkDocument MapToDocument(ChunkRequest request, Chunk chunk)
    {
        return new IndexedChunkDocument
        {
            ChunkId = ComputeChunkId(
                request.MachineId,
                request.DocumentId,
                chunk.PageStart,
                chunk.PageEnd,
                chunk.ChunkIndex),
            MachineId = request.MachineId,
            MachineTitle = request.MachineTitle,
            Manufacturer = request.Manufacturer,
            DocumentId = request.DocumentId,
            DocumentUrl = request.DocumentUrl,
            DocumentType = request.DocumentType.ToString(),
            PageStart = chunk.PageStart,
            PageEnd = chunk.PageEnd,
            SectionHeading = chunk.SectionHeading,
            Content = chunk.Text,
            ContentEmbedding = [], // populated post-embed
            // LastScrapedUtc carries Timeline.LastDownloadedAt from the
            // Phase 1 scraper provenance record (PR-C3). Null for legacy
            // chunks indexed before PR-C3 — acceptable per ADR-0025 § 6
            // (zero-migration-cost: existing chunks update on next
            // Change Feed re-ingestion; no backfill required).
            LastScrapedUtc = request.LastScrapedUtc,
            // edition + edition_scope threaded from the scraped_documents
            // provenance record (Task 6, AB#259) so each chunk self-declares
            // its edition + scope for retriever filtering / Wizard R1/R2/R3.
            // Null for legacy / unresolved documents (acceptable per
            // ADR-0025 § 6 zero-migration-cost).
            Edition = request.Edition,
            EditionScope = request.EditionScope,
        };
    }

    internal static IReadOnlyList<(int Start, int Count)> BatchIndices(int total, int batchSize)
    {
        if (total <= 0 || batchSize <= 0)
        {
            return [];
        }

        var batches = new List<(int Start, int Count)>(capacity: (total + batchSize - 1) / batchSize);
        for (int start = 0; start < total; start += batchSize)
        {
            var count = Math.Min(batchSize, total - start);
            batches.Add((start, count));
        }
        return batches;
    }

    private static void ValidateOptions(RagIndexerOptions options)
    {
        if (options.BatchSize <= 0 || options.BatchSize > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.BatchSize,
                "BatchSize must be in (0, 1000]; AI Search caps batched upserts at 1000 documents.");
        }
        if (options.IndexUploadConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.IndexUploadConcurrency,
                "IndexUploadConcurrency must be positive.");
        }
        if (options.EmbeddingMaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.EmbeddingMaxConcurrency,
                "EmbeddingMaxConcurrency must be positive.");
        }
        if (options.EmbeddingBatchSize <= 0)
        {
            // A non-positive value makes BatchIndices yield no sub-batches, which
            // would silently upload zero-length embeddings (corrupt index). Fail loud.
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.EmbeddingBatchSize,
                "EmbeddingBatchSize must be positive.");
        }
    }
}
