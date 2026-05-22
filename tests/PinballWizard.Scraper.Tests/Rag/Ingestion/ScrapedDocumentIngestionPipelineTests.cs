using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Ingestion;

// Behavior tests for the W3-2 ScrapedDocumentIngestionPipeline.
// Targets the two-filter path (document-type → hash) plus the extract /
// chunk / index / state-record happy path. All dependencies are NSubstitute
// fakes; no Cosmos, no AI Search, no embedding model.
public sealed class ScrapedDocumentIngestionPipelineTests
{
    private const string TestMachineId = "GRBN-MQR4P";

    [Fact]
    public async Task IngestAsync_DocumentTypeFiltered_ReturnsDocumentTypeFiltered_NoExtraction()
    {
        // Filter 2: the curated machine is in scope but the document
        // type isn't (e.g., a Schematic where only Manual + ServiceBulletin
        // are accepted).
        var fakes = new Fakes();
        var pipeline = fakes.BuildPipeline();

        var change = NewChange(documentType: DocumentType.Schematic);
        await using var stream = NewStream();

        var outcome = await pipeline.IngestAsync(change, stream, CancellationToken.None);

        Assert.Equal(IngestionOutcome.Skipped_DocumentTypeFiltered, outcome);
        await fakes.Extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact]
    public async Task IngestAsync_HashUnchanged_ReturnsHashUnchanged_NoExtraction()
    {
        // Filter 3 is the dominant cost saver: a re-poll that bumps
        // metadata without changing the body must NOT re-embed.
        var fakes = new Fakes();
        fakes.IndexState.GetLastIndexedHashAsync("doc_1", Arg.Any<CancellationToken>())
            .Returns("hash-123");

        var pipeline = fakes.BuildPipeline();
        var change = NewChange(documentId: "doc_1", contentHash: "hash-123");
        await using var stream = NewStream();

        var outcome = await pipeline.IngestAsync(change, stream, CancellationToken.None);

        Assert.Equal(IngestionOutcome.Skipped_HashUnchanged, outcome);
        await fakes.Extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact]
    public async Task IngestAsync_HashFirstSeen_ProceedsToExtractionAndIndexes()
    {
        // No prior state row → null lastHash → don't short-circuit;
        // proceed through the full happy path.
        var fakes = new Fakes();
        fakes.IndexState.GetLastIndexedHashAsync("doc_1", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        fakes.SetExtractionSuccess(pages: 3);
        fakes.SetChunkingResult(chunkCount: 5);
        fakes.SetIndexingSuccess(indexed: 5, failures: 0);

        var pipeline = fakes.BuildPipeline();
        var change = NewChange(documentId: "doc_1", contentHash: "hash-new");
        await using var stream = NewStream();

        var outcome = await pipeline.IngestAsync(change, stream, CancellationToken.None);

        Assert.Equal(IngestionOutcome.Indexed, outcome);
        await fakes.Indexer.Received(1).UpsertAsync(
            Arg.Is<ChunkRequest>(r =>
                r.MachineId == TestMachineId
                && r.DocumentId == "doc_1"
                && r.DocumentType == DocumentType.Manual),
            Arg.Any<IReadOnlyList<Chunk>>(),
            Arg.Any<RagIndexerOptions>(),
            Arg.Any<CancellationToken>());
        await fakes.IndexState.Received(1).RecordIndexedAsync(
            "doc_1", "hash-new", 5, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_HashChanged_ProceedsToExtractionAndIndexes()
    {
        // Prior hash exists but differs from the change's ContentHash
        // → don't short-circuit; the body changed so re-embedding is
        // mandatory.
        var fakes = new Fakes();
        fakes.IndexState.GetLastIndexedHashAsync("doc_1", Arg.Any<CancellationToken>())
            .Returns("hash-old");
        fakes.SetExtractionSuccess();
        fakes.SetChunkingResult(chunkCount: 2);
        fakes.SetIndexingSuccess(indexed: 2, failures: 0);

        var pipeline = fakes.BuildPipeline();
        var change = NewChange(documentId: "doc_1", contentHash: "hash-new");
        await using var stream = NewStream();

        var outcome = await pipeline.IngestAsync(change, stream, CancellationToken.None);

        Assert.Equal(IngestionOutcome.Indexed, outcome);
    }

    [Theory]
    [InlineData(ExtractionStatus.OcrRequired)]
    [InlineData(ExtractionStatus.Encrypted)]
    [InlineData(ExtractionStatus.Malformed)]
    [InlineData(ExtractionStatus.SizeExceeded)]
    public async Task IngestAsync_NonSuccessExtraction_SkipsWithoutIndexing(ExtractionStatus status)
    {
        // Each non-Success extraction status is a known coverage gap
        // (per Phase 4 § Non-goals). The pipeline logs and skips at
        // the per-document level; the batch advances. State is NOT
        // recorded — re-delivery should re-evaluate (the next deploy
        // might bring an OCR fallback for this document).
        var fakes = new Fakes();
        fakes.IndexState.GetLastIndexedHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        fakes.SetExtractionFailure(status, error: $"simulated {status}");

        var pipeline = fakes.BuildPipeline();
        var change = NewChange();
        await using var stream = NewStream();

        var outcome = await pipeline.IngestAsync(change, stream, CancellationToken.None);

        Assert.Equal(IngestionOutcome.Skipped_ExtractionFailed, outcome);
        fakes.Chunker.DidNotReceiveWithAnyArgs().Chunk(default!, default!);
        await fakes.Indexer.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default!, default!, default);
        await fakes.IndexState.DidNotReceiveWithAnyArgs().RecordIndexedAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task IngestAsync_ChunkerProducesZeroChunks_RecordsStateAndReturnsIndexed()
    {
        // Defensive: chunker is supposed to produce ≥1 chunk for any
        // successful extraction, but if it returns zero we still record
        // state to prevent a re-delivery retry loop. Indexer must NOT
        // be called (no chunks to upsert).
        var fakes = new Fakes();
        fakes.IndexState.GetLastIndexedHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        fakes.SetExtractionSuccess();
        fakes.SetChunkingResult(chunkCount: 0);

        var pipeline = fakes.BuildPipeline();
        var change = NewChange(contentHash: "hash-z");
        await using var stream = NewStream();

        var outcome = await pipeline.IngestAsync(change, stream, CancellationToken.None);

        Assert.Equal(IngestionOutcome.Indexed, outcome);
        await fakes.Indexer.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default!, default!, default);
        await fakes.IndexState.Received(1).RecordIndexedAsync(
            change.DocumentId, "hash-z", 0, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_PartialIndexFailure_RecordsFailureCount_StillReturnsIndexed()
    {
        // AI Search rejecting one chunk (e.g., schema validation) is
        // operationally common. The pipeline records the failure count
        // on state but the document still counts as Indexed —
        // dead-letter escalation lives in the hosted service when
        // failureCount exceeds RagIngestionOptions.MaxFailuresPerDocument.
        var fakes = new Fakes();
        fakes.IndexState.GetLastIndexedHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        fakes.SetExtractionSuccess();
        fakes.SetChunkingResult(chunkCount: 4);
        fakes.SetIndexingSuccess(indexed: 3, failures: 1);

        var pipeline = fakes.BuildPipeline();
        var change = NewChange(contentHash: "hash-p");
        await using var stream = NewStream();

        var outcome = await pipeline.IngestAsync(change, stream, CancellationToken.None);

        Assert.Equal(IngestionOutcome.Indexed, outcome);
        await fakes.IndexState.Received(1).RecordIndexedAsync(
            change.DocumentId, "hash-p", 3, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_BuildsChunkRequestFromChangeMetadata()
    {
        // ChunkRequest fields flow from ScrapedDocumentChange one-to-one
        // (machine id, title, manufacturer, document id, document url,
        // document type). Pinned because a future refactor that drops
        // a field would silently break the citation surface (the
        // page-anchored citation depends on DocumentUrl flowing through
        // unchanged to AI Search).
        var fakes = new Fakes();
        fakes.IndexState.GetLastIndexedHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        fakes.SetExtractionSuccess();
        fakes.SetChunkingResult(chunkCount: 1);
        fakes.SetIndexingSuccess(indexed: 1, failures: 0);

        var pipeline = fakes.BuildPipeline();
        var change = new ScrapedDocumentChange(
            DocumentId: "doc_x",
            DocumentUrl: "https://example/foo.pdf",
            MachineId: TestMachineId,
            MachineTitle: "Foo Fighters",
            Manufacturer: "Stern Pinball",
            DocumentType: DocumentType.ServiceBulletin,
            ContentHash: "hash-y");
        await using var stream = NewStream();

        await pipeline.IngestAsync(change, stream, CancellationToken.None);

        fakes.Chunker.Received(1).Chunk(
            Arg.Any<ExtractedDocument>(),
            Arg.Is<ChunkRequest>(r =>
                r.MachineId == TestMachineId
                && r.MachineTitle == "Foo Fighters"
                && r.Manufacturer == "Stern Pinball"
                && r.DocumentId == "doc_x"
                && r.DocumentUrl == "https://example/foo.pdf"
                && r.DocumentType == DocumentType.ServiceBulletin),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_NullChange_Throws()
    {
        var fakes = new Fakes();
        var pipeline = fakes.BuildPipeline();
        await using var stream = NewStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            pipeline.IngestAsync(null!, stream, CancellationToken.None));
    }

    [Fact]
    public async Task IngestAsync_NullStream_Throws()
    {
        var fakes = new Fakes();
        var pipeline = fakes.BuildPipeline();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            pipeline.IngestAsync(NewChange(), null!, CancellationToken.None));
    }

    [Fact]
    public void Ctor_NullExtractor_Throws()
    {
        var fakes = new Fakes();
        Assert.Throws<ArgumentNullException>(() => new ScrapedDocumentIngestionPipeline(
            null!,
            fakes.Chunker,
            fakes.Indexer,
            fakes.IndexState,
            fakes.IngestionOptions,
            fakes.IndexerOptions,
            NullLogger<ScrapedDocumentIngestionPipeline>.Instance));
    }

    // ────────────────────────────────────────────────────────────────
    // Test fixture
    // ────────────────────────────────────────────────────────────────

    private static ScrapedDocumentChange NewChange(
        string documentId = "doc_default",
        string machineId = TestMachineId,
        DocumentType documentType = DocumentType.Manual,
        string contentHash = "hash-default") =>
        new(
            DocumentId: documentId,
            DocumentUrl: $"https://example/{documentId}.pdf",
            MachineId: machineId,
            MachineTitle: "Foo Fighters",
            Manufacturer: "Stern Pinball",
            DocumentType: documentType,
            ContentHash: contentHash);

    private static MemoryStream NewStream() => new(Encoding.UTF8.GetBytes("PDF-PLACEHOLDER"));

    private sealed class Fakes
    {
        public IDocumentTextExtractor Extractor { get; } = Substitute.For<IDocumentTextExtractor>();
        public IChunker Chunker { get; } = Substitute.For<IChunker>();
        public IRagIndexer Indexer { get; } = Substitute.For<IRagIndexer>();
        public IIndexState IndexState { get; } = Substitute.For<IIndexState>();

        public IOptions<RagIngestionOptions> IngestionOptions { get; } = Options.Create(new RagIngestionOptions
        {
            AcceptedDocumentTypes = [DocumentType.Manual, DocumentType.ServiceBulletin],
        });

        public IOptions<RagIndexerOptions> IndexerOptions { get; } = Options.Create(new RagIndexerOptions());

        public ScrapedDocumentIngestionPipeline BuildPipeline() => new(
            Extractor,
            Chunker,
            Indexer,
            IndexState,
            IngestionOptions,
            IndexerOptions,
            NullLogger<ScrapedDocumentIngestionPipeline>.Instance);

        public void SetExtractionSuccess(int pages = 1)
        {
            var pageList = Enumerable.Range(1, pages)
                .Select(i => new ExtractedPage(i, $"page {i} text"))
                .ToList();
            Extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new ExtractedDocument(
                    Status: ExtractionStatus.Success,
                    Text: string.Join("\n", pageList.Select(p => p.Text)),
                    Pages: pageList,
                    Outline: [],
                    Error: null));
        }

        public void SetExtractionFailure(ExtractionStatus status, string error)
        {
            Extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(ExtractedDocument.Failure(status, error));
        }

        public void SetChunkingResult(int chunkCount)
        {
            var chunks = Enumerable.Range(0, chunkCount)
                .Select(i => new Chunk(
                    ChunkIndex: i,
                    Text: $"chunk {i}",
                    SectionHeading: $"Section {i}",
                    PageStart: i + 1,
                    PageEnd: i + 1,
                    TokenCount: 50))
                .ToList();
            Chunker.Chunk(Arg.Any<ExtractedDocument>(), Arg.Any<ChunkRequest>(), Arg.Any<CancellationToken>())
                .Returns(chunks);
        }

        public void SetIndexingSuccess(int indexed, int failures)
        {
            var failureList = Enumerable.Range(0, failures)
                .Select(i => new IndexUpsertFailure($"chunk_failed_{i}", 422, "schema validation"))
                .ToList<IndexUpsertFailure>();
            Indexer.UpsertAsync(
                Arg.Any<ChunkRequest>(),
                Arg.Any<IReadOnlyList<Chunk>>(),
                Arg.Any<RagIndexerOptions>(),
                Arg.Any<CancellationToken>())
                .Returns(new IndexUpsertResult(indexed, failureList));
        }
    }
}
