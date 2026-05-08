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

namespace PinballWizard.Scraper.Tests.Rag.Indexing;

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
        DocumentType: DocumentType.Manual);

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

        var samples = new List<(double Value, string? DocumentTypeTag)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == PinballWizardTelemetry.RagIndexingDurationMs.Name)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
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

        var samples = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == PinballWizardTelemetry.RagIndexingDurationMs.Name)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            samples.Add(value));
        listener.Start();

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
                new RagIndexerOptions(BatchSize: 0), CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions(BatchSize: 2000), CancellationToken.None));
    }

    [Fact]
    public async Task UpsertAsync_InvalidConcurrency_Throws()
    {
        var embedder = Substitute.For<IChunkEmbedder>();
        var sut = NewIndexer(embedder);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions(IndexUploadConcurrency: 0), CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.UpsertAsync(SampleRequest, [MakeChunk(0)],
                new RagIndexerOptions(EmbeddingMaxConcurrency: 0), CancellationToken.None));
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
