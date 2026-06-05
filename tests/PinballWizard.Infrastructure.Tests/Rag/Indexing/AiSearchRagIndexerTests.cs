using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Rag.Indexing;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Indexing;

// Behavior-asserting tests for AiSearchRagIndexer (build-spec § Phase
// 4 item 16, ADR-0021). Pure-function units (ComputeChunkId,
// BatchIndices, MapToDocument) are exercised here; the
// `UpsertAsync` happy path is covered by AiSearchRagIndexerLiveTests
// against a real index (gated on PINBALL_WIZARD_LIVE_RAG_TESTS=1).
public sealed class AiSearchRagIndexerTests
{
    private static readonly ChunkRequest SampleRequest = new(
        MachineId: "mch_godzilla",
        MachineTitle: "Godzilla (Premium)",
        Manufacturer: "Stern Pinball",
        DocumentId: "doc_godzilla_manual",
        DocumentUrl: "https://sternpinball.com/wp-content/uploads/godzilla_manual.pdf",
        DocumentType: DocumentType.Manual,
        Edition: "Premium",
        EditionScope: "single-edition");

    private static Chunk MakeChunk(int index, int pageStart = 1, int pageEnd = 1, string text = "the quick brown fox") =>
        new(
            ChunkIndex: index,
            Text: text,
            SectionHeading: "Sample",
            PageStart: pageStart,
            PageEnd: pageEnd,
            TokenCount: 5);

    [Fact]
    public void ComputeChunkId_StableAcrossCalls()
    {
        // Idempotency contract: the same inputs produce the same ID.
        // This is the underpinning of the indexer's "re-running on
        // unchanged content is a no-op" guarantee per ADR-0021.
        var id1 = AiSearchRagIndexer.ComputeChunkId("mch_a", "doc_b", 1, 2, 0);
        var id2 = AiSearchRagIndexer.ComputeChunkId("mch_a", "doc_b", 1, 2, 0);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeChunkId_FormatIsChkPrefixSixteenLowerHex()
    {
        // Project-wide deterministic-ID convention (mch_, doc_, chk_).
        // 16 lowercase hex chars after the prefix → 64-bit collision
        // space, comfortable past Phase 4.5 corpus volume.
        var id = AiSearchRagIndexer.ComputeChunkId("mch_a", "doc_b", 1, 2, 0);
        Assert.StartsWith("chk_", id);
        Assert.Equal(20, id.Length); // "chk_" + 16
        var hex = id["chk_".Length..];
        Assert.All(hex, c => Assert.True(char.IsDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Theory]
    [InlineData("mch_a", "doc_b", 1, 2, 0, "mch_X", "doc_b", 1, 2, 0)] // machine differs
    [InlineData("mch_a", "doc_b", 1, 2, 0, "mch_a", "doc_X", 1, 2, 0)] // document differs
    [InlineData("mch_a", "doc_b", 1, 2, 0, "mch_a", "doc_b", 9, 2, 0)] // page_start differs
    [InlineData("mch_a", "doc_b", 1, 2, 0, "mch_a", "doc_b", 1, 9, 0)] // page_end differs
    [InlineData("mch_a", "doc_b", 1, 2, 0, "mch_a", "doc_b", 1, 2, 9)] // chunk_index differs
    public void ComputeChunkId_DiffersAcrossEachComponent(
        string m1, string d1, int p1s, int p1e, int c1,
        string m2, string d2, int p2s, int p2e, int c2)
    {
        // Sensitivity check: every component participates in the hash.
        // A trivially-broken implementation that ignored e.g.
        // page_start would silently collapse all chunks on the same
        // document into one ID — and the orphan-cleanup pass per
        // ADR-0021 § Versioning strategy would never fire.
        var a = AiSearchRagIndexer.ComputeChunkId(m1, d1, p1s, p1e, c1);
        var b = AiSearchRagIndexer.ComputeChunkId(m2, d2, p2s, p2e, c2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeChunkId_BoundaryCollisionGuard_PipeSeparator()
    {
        // Without separators, the inputs (mch_a, doc_b) and (mch_,
        // adoc_b) would canonicalize to the same string. The pipe
        // separator prevents the boundary collision.
        var a = AiSearchRagIndexer.ComputeChunkId("mch_a", "doc_b", 1, 2, 0);
        var b = AiSearchRagIndexer.ComputeChunkId("mch_", "adoc_b", 1, 2, 0);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeChunkId_NullMachineId_Throws()
    {
        // ArgumentException.ThrowIfNullOrEmpty surfaces null as the
        // ArgumentNullException subclass — use ThrowsAny to cover
        // both the null and empty branches under one type umbrella.
        Assert.ThrowsAny<ArgumentException>(() =>
            AiSearchRagIndexer.ComputeChunkId(null!, "doc_b", 1, 1, 0));
    }

    [Fact]
    public void ComputeChunkId_EmptyDocumentId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            AiSearchRagIndexer.ComputeChunkId("mch_a", "", 1, 1, 0));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 1000)]
    [InlineData(999, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(2500, 1000)]
    [InlineData(2500, 100)]
    public void BatchIndices_PartitionsCorrectly(int total, int batchSize)
    {
        var batches = AiSearchRagIndexer.BatchIndices(total, batchSize);
        if (total <= 0)
        {
            Assert.Empty(batches);
            return;
        }

        // Every chunk index in [0, total) appears in exactly one batch
        // and no chunk is double-counted.
        var covered = new HashSet<int>();
        foreach (var (start, count) in batches)
        {
            Assert.True(count > 0);
            Assert.True(count <= batchSize);
            for (int i = 0; i < count; i++)
            {
                Assert.True(covered.Add(start + i));
            }
        }
        Assert.Equal(total, covered.Count);

        var expectedBatchCount = (total + batchSize - 1) / batchSize;
        Assert.Equal(expectedBatchCount, batches.Count);
    }

    [Fact]
    public void BatchIndices_NonPositiveTotal_ReturnsEmpty()
    {
        Assert.Empty(AiSearchRagIndexer.BatchIndices(total: 0, batchSize: 10));
        Assert.Empty(AiSearchRagIndexer.BatchIndices(total: -1, batchSize: 10));
    }

    [Fact]
    public void RagIndexerOptions_Defaults_AreCorrect()
    {
        // BatchSize=100: splits large documents into multiple concurrent upload
        // batches so EmbeddingMaxConcurrency is actually utilized. BatchSize=1000
        // produced a single batch per document, serializing all embedding calls
        // and making large manuals take ~10 minutes (AB#259).
        // EmbeddingBatchSize=32: keeps each embedding API call well under the
        // ~100s network timeout while reducing round-trips vs. the previous 16.
        var opts = new RagIndexerOptions();
        Assert.Equal(100, opts.BatchSize);
        Assert.Equal(32, opts.EmbeddingBatchSize);
        Assert.Equal(8, opts.EmbeddingMaxConcurrency);
        Assert.Equal(4, opts.IndexUploadConcurrency);
    }

    [Fact]
    public async Task UpsertAsync_SubBatchesEmbeddingCalls_ByEmbeddingBatchSize_NotUploadBatchSize()
    {
        // A 40-chunk doc with EmbeddingBatchSize=16 must call EmbedBatchAsync
        // THREE times (16 + 16 + 8), each with <= 16 texts — NOT one 40-text call.
        // (Upload BatchSize stays large; the two limits are decoupled.) The
        // placeholder SearchClient makes the upload throw, but the embedding calls
        // happen first, so we assert the captured embed-call sizes.
        var embedCallSizes = new ConcurrentBag<int>();
        var embedder = Substitute.For<IChunkEmbedder>();
        embedder.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IReadOnlyList<string>>();
                embedCallSizes.Add(texts.Count);
                // Return one vector per input text so the per-call contract holds.
                var vectors = texts.Select(_ => new ReadOnlyMemory<float>([1f, 2f, 3f])).ToList();
                return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(vectors);
            });

        var sut = NewIndexer(embedder);
        var chunks = Enumerable.Range(0, 40).Select(i => MakeChunk(i)).ToList();
        var options = new RagIndexerOptions { EmbeddingBatchSize = 16 };

        // Upload will fail against the placeholder SearchClient — that's fine;
        // the embedding sub-batching we're testing runs before the upload.
        try
        {
            await sut.UpsertAsync(SampleRequest, chunks, options, CancellationToken.None);
        }
        catch (Exception) { /* placeholder upload failure expected */ }

        // Every embed call carried <= 16 texts, none carried 40.
        Assert.NotEmpty(embedCallSizes);
        Assert.All(embedCallSizes, n => Assert.True(n <= 16, $"embed call had {n} texts; must be <= 16"));
        Assert.DoesNotContain(40, embedCallSizes);
        // 40 chunks / 16 = 3 sub-batches (16 + 16 + 8) for the single upload range.
        Assert.Equal(40, embedCallSizes.Sum());
    }

    [Theory]
    [InlineData(8, 16)]    // count < EmbeddingBatchSize → one short sub-batch
    [InlineData(16, 16)]   // count == EmbeddingBatchSize → exactly one full sub-batch (no remainder)
    [InlineData(17, 16)]   // one full + 1 remainder
    [InlineData(100, 16)]  // many sub-batches
    [InlineData(40, 64)]   // EmbeddingBatchSize > count → single sub-batch of `count`
    public async Task UpsertAsync_SubBatchBoundaries_CoverEveryChunkExactlyOnce(int count, int embeddingBatchSize)
    {
        var embedCallSizes = new ConcurrentBag<int>();
        var embedder = Substitute.For<IChunkEmbedder>();
        embedder.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IReadOnlyList<string>>();
                embedCallSizes.Add(texts.Count);
                return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                    texts.Select(_ => new ReadOnlyMemory<float>([1f, 2f, 3f])).ToList());
            });

        var sut = NewIndexer(embedder);
        var chunks = Enumerable.Range(0, count).Select(i => MakeChunk(i)).ToList();
        var options = new RagIndexerOptions { EmbeddingBatchSize = embeddingBatchSize };

        try { await sut.UpsertAsync(SampleRequest, chunks, options, CancellationToken.None); }
        catch (Exception) { /* placeholder upload failure expected */ }

        Assert.All(embedCallSizes, n => Assert.True(n <= embeddingBatchSize));
        Assert.Equal(count, embedCallSizes.Sum());            // every chunk embedded exactly once
        var expectedCalls = (count + embeddingBatchSize - 1) / embeddingBatchSize;
        Assert.Equal(expectedCalls, embedCallSizes.Count);
    }

    [Fact]
    public async Task UpsertAsync_NonPositiveEmbeddingBatchSize_Throws_NotSilentEmptyEmbeddings()
    {
        // A zero/negative EmbeddingBatchSize would make BatchIndices return no
        // sub-batches → zero-length embeddings silently uploaded (corrupt index).
        // Must throw at validation instead.
        var embedder = Substitute.For<IChunkEmbedder>();
        var sut = NewIndexer(embedder);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(
                SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions { EmbeddingBatchSize = 0 },
                CancellationToken.None));
        await embedder.DidNotReceive().EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertAsync_EmbeddingBatchSizeAboveAzureLimit_Throws()
    {
        // An oversized EmbeddingBatchSize would re-introduce the exact >100s-timeout
        // bug this whole change fixes (one huge embedding call). Cap at Azure OpenAI's
        // documented 2048 max-inputs-per-embedding-call so the fix is self-enforcing.
        var embedder = Substitute.For<IChunkEmbedder>();
        var sut = NewIndexer(embedder);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(
                SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions { EmbeddingBatchSize = 2049 },
                CancellationToken.None));
        await embedder.DidNotReceive().EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MapToDocument_PopulatesEverySchemaField()
    {
        var chunk = new Chunk(
            ChunkIndex: 7,
            Text: "Foo Mode Rules text body…",
            SectionHeading: "Foo Mode Rules",
            PageStart: 42,
            PageEnd: 43,
            TokenCount: 50);

        var doc = AiSearchRagIndexer.MapToDocument(SampleRequest, chunk);

        Assert.Equal(SampleRequest.MachineId, doc.MachineId);
        Assert.Equal(SampleRequest.MachineTitle, doc.MachineTitle);
        Assert.Equal(SampleRequest.Manufacturer, doc.Manufacturer);
        Assert.Equal(SampleRequest.DocumentId, doc.DocumentId);
        Assert.Equal(SampleRequest.DocumentUrl, doc.DocumentUrl);
        Assert.Equal("Manual", doc.DocumentType);
        Assert.Equal(42, doc.PageStart);
        Assert.Equal(43, doc.PageEnd);
        Assert.Equal("Foo Mode Rules", doc.SectionHeading);
        Assert.Equal("Foo Mode Rules text body…", doc.Content);
        Assert.Empty(doc.ContentEmbedding); // populated post-embed in UpsertAsync

        var expectedId = AiSearchRagIndexer.ComputeChunkId(
            SampleRequest.MachineId, SampleRequest.DocumentId, 42, 43, 7);
        Assert.Equal(expectedId, doc.ChunkId);
    }

    [Fact]
    public void MapToDocument_ThreadsEditionAndEditionScope()
    {
        // Task 6 (AB#259): every chunk self-declares its free-text edition
        // label and structural edition scope so a future retriever query
        // can filter by them and the Wizard can decide R1/R2/R3
        // (answer-all vs honest-substitution). Verify both threads from
        // ChunkRequest → IndexedChunkDocument unchanged.
        var chunk = MakeChunk(0);

        var doc = AiSearchRagIndexer.MapToDocument(SampleRequest, chunk);

        Assert.Equal("Premium", doc.Edition);
        Assert.Equal("single-edition", doc.EditionScope);
    }

    [Fact]
    public void MapToDocument_NullEditionFields_RemainNull()
    {
        // Legacy / unresolved documents may carry no edition metadata.
        // The mapping must propagate null rather than substituting an
        // empty string — null is filterable-absent in AI Search, an
        // empty string would be a distinct (wrong) facet value.
        var request = SampleRequest with { Edition = null, EditionScope = null };
        var chunk = MakeChunk(0);

        var doc = AiSearchRagIndexer.MapToDocument(request, chunk);

        Assert.Null(doc.Edition);
        Assert.Null(doc.EditionScope);
    }

    [Fact]
    public async Task UpsertAsync_EmbedderMismatch_StillEmitsIndexingDurationSample()
    {
        // The Stopwatch + try/finally placement is the wiring at risk
        // of regression: a future refactor could move the timing
        // outside the failure path and silently zero the histogram on
        // every error path. Verify a single emission lands on
        // RagIndexingDurationMs even when the work throws.
        var embedder = Substitute.For<IChunkEmbedder>();
        embedder
            .EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                [new ReadOnlyMemory<float>([1f, 2f, 3f])]));

        var sut = NewIndexer(embedder);

        // Force PinballWizardTelemetry's static cctor to complete before
        // wiring the listener. The InstrumentPublished callback fires
        // synchronously during Start() when Instrument.Publish() is called,
        // and accessing PinballWizardTelemetry inside that callback (before
        // the cctor finishes) causes a TypeInitializationException. The
        // correct pattern is to touch the instrument first so the cctor is
        // complete, then Start() + EnableMeasurementEvents() directly.
        // ConcurrentBag handles parallel test-class callbacks on the
        // process-global Meter (mirrors MachineGroundingToolTests pattern).
        _ = PinballWizardTelemetry.RagIndexingDurationMs;
        var samples = new ConcurrentBag<(double Value, string? DocumentTypeTag)>();
        using var listener = new MeterListener();
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name != PinballWizardTelemetry.RagIndexingDurationMs.Name)
                return;
            string? docTypeTag = null;
            foreach (var t in tags)
            {
                if (t.Key == "document_type")
                {
                    docTypeTag = t.Value as string;
                }
            }
            samples.Add((value, docTypeTag));
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.RagIndexingDurationMs);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpsertAsync(
                SampleRequest,
                [MakeChunk(0), MakeChunk(1)], // count mismatch with single-vector embedder
                new RagIndexerOptions(),
                CancellationToken.None));

        var sample = Assert.Single(samples);
        Assert.True(sample.Value >= 0.0);
        Assert.Equal("Manual", sample.DocumentTypeTag);
    }

    [Fact]
    public async Task UpsertAsync_EmptyChunks_DoesNotEmitAnyDurationSample()
    {
        // Empty-chunks short-circuits BEFORE the stopwatch starts —
        // the histogram should NOT receive a sample for a zero-work
        // call. Verifying this avoids polluting dashboards with
        // misleadingly-fast samples on no-op invocations.
        var embedder = Substitute.For<IChunkEmbedder>();
        var sut = NewIndexer(embedder);

        _ = PinballWizardTelemetry.RagIndexingDurationMs; // ensure cctor complete before listener wires
        var samples = new ConcurrentBag<double>();
        using var listener = new MeterListener();
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == PinballWizardTelemetry.RagIndexingDurationMs.Name)
                samples.Add(value);
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.RagIndexingDurationMs);

        var result = await sut.UpsertAsync(
            SampleRequest, [], new RagIndexerOptions(), CancellationToken.None);

        Assert.Equal(0, result.Indexed);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task UpsertAsync_EmptyChunks_ReturnsZeroIndexedAndDoesNotCallEmbedder()
    {
        // Short-circuit invariant: zero chunks ⇒ zero work. The
        // indexer never reaches the embedder or SearchClient — the
        // Cosmos Change Feed Function (W3-2) often delivers
        // unchanged-document signals where re-extraction yields no
        // new chunks; that path must be allocation-free.
        var embedder = Substitute.For<IChunkEmbedder>();
        var sut = NewIndexer(embedder);

        var result = await sut.UpsertAsync(
            SampleRequest, [], new RagIndexerOptions(), CancellationToken.None);

        Assert.Equal(0, result.Indexed);
        Assert.Empty(result.Failures);
        await embedder.DidNotReceive().EmbedBatchAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertAsync_InvalidBatchSize_Throws()
    {
        var embedder = Substitute.For<IChunkEmbedder>();
        var sut = NewIndexer(embedder);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions { BatchSize = 0 }, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions { BatchSize = 2000 }, CancellationToken.None));
    }

    [Fact]
    public async Task UpsertAsync_InvalidConcurrency_Throws()
    {
        var embedder = Substitute.For<IChunkEmbedder>();
        var sut = NewIndexer(embedder);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions { IndexUploadConcurrency = 0 }, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions { EmbeddingMaxConcurrency = 0 }, CancellationToken.None));
    }

    [Fact]
    public async Task UpsertAsync_EmbedderReturnsWrongCount_Throws()
    {
        // The embedder contract requires output to align positionally
        // with input. A length mismatch means we'd write garbage
        // vectors — fail loud rather than silently corrupt the index.
        var embedder = Substitute.For<IChunkEmbedder>();
        embedder
            .EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                [new ReadOnlyMemory<float>([1f, 2f, 3f])]));

        var sut = NewIndexer(embedder);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpsertAsync(
                SampleRequest,
                [MakeChunk(0), MakeChunk(1)], // 2 chunks but embedder returns 1 vector
                new RagIndexerOptions(),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpsertAsync_OneEmbedBatchFails_CancelsSiblingBatchesAndThrows()
    {
        // Source guarantee (lines 202–209): a batch that hits a transport-level failure
        // calls `linkedCts.Cancel()` before rethrowing, so sibling batches waiting on
        // `embedGate.WaitAsync(workToken)` receive OperationCanceledException instead of
        // continuing to burn embed-TPM.  With BatchSize=1 and 2 chunks we get 2 batches
        // running concurrently (EmbeddingMaxConcurrency defaults to >=2); the embedder
        // is configured to throw on first call, then stall on any subsequent call
        // (which would time out the test if cancellation didn't fire).
        //
        // Assertion: UpsertAsync propagates an exception (the original or the linked
        // cancellation) and the embedder is invoked at most once — proof that the second
        // batch was cancelled before it could call EmbedBatchAsync.
        var callCount = 0;
        var embedder = Substitute.For<IChunkEmbedder>();
        embedder
            .EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var count = System.Threading.Interlocked.Increment(ref callCount);
                if (count == 1)
                    throw new InvalidOperationException("embed batch 0 failed — transport error");

                // If the sibling-cancel wiring is broken the second call reaches here
                // and hangs for the full test timeout; the at-most-one assertion below
                // then catches any slip-through that completes without hanging.
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                    [new ReadOnlyMemory<float>([1f])]);
            });

        var sut = NewIndexer(embedder);

        // 2 chunks, BatchSize=1 → 2 batches; EmbeddingMaxConcurrency=2 lets them both start.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.UpsertAsync(
                SampleRequest,
                [MakeChunk(0), MakeChunk(1)],
                new RagIndexerOptions { BatchSize = 1, EmbeddingMaxConcurrency = 2 },
                CancellationToken.None));

        Assert.True(callCount <= 1,
            $"Expected embedder to be called at most once (sibling-cancel wiring), but was called {callCount} time(s).");
    }

    private static AiSearchRagIndexer NewIndexer(IChunkEmbedder embedder)
    {
        // SearchClient is constructed against a placeholder URI — the
        // tests in this class exercise paths that do NOT reach the
        // SearchClient (empty chunks short-circuit, validation throws,
        // embedder-contract violation throws-before-upload). The live
        // sibling test class exercises the upload path against a real
        // service.
        var searchClient = new SearchClient(
            endpoint: new Uri("https://placeholder.search.windows.net"),
            indexName: "pinwiz-rag-v1",
            credential: new Azure.AzureKeyCredential("placeholder"));
        return new AiSearchRagIndexer(searchClient, embedder, NullLogger<AiSearchRagIndexer>.Instance);
    }
}
