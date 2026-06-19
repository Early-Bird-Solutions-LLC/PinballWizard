using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

// Behavior tests for the ScrapedDocumentChangeFeedHandler bridge —
// the Infrastructure adapter between Cosmos's RagSourceDocument
// projection and the Application-layer IRagIngestionPipeline.
//
// Drives the bridge end-to-end against the in-memory fakes from
// InMemoryFakes.cs PLUS NSubstitute fakes for the Application
// abstractions the pipeline composes (extractor + chunker). The
// pipeline itself runs for real here — these tests pin the bridge's
// document-type parsing + bytes-source invocation + ChunkRequest
// construction.
public sealed class ScrapedDocumentChangeFeedHandlerTests
{
    private const string TestMachineId = "GRBN-MQR4P"; // arbitrary stable ID for test assertions

    [Fact]
    public async Task HandleAsync_HappyPath_InvokesPipelineWithMappedChange()
    {
        var ctx = new TestContext();
        ctx.SeedExtractionAndChunking();

        var change = NewChange(documentId: "doc_x", contentHash: "hash-x");
        await ctx.Handler.HandleAsync(change, CancellationToken.None);

        var call = Assert.Single(ctx.Indexer.Calls);
        Assert.Equal("doc_x", call.DocumentId);
        Assert.Equal(TestMachineId, call.MachineId);
        // Bytes source invoked once with the source URL.
        Assert.Contains("https://example/doc_x.pdf", ctx.BytesSource.Calls);
    }

    [Fact]
    public async Task HandleAsync_ThreadsEditionAndEditionScopeIntoChunkRequest()
    {
        // Task 6 (AB#259): the read-side projection (RagSourceDocument) must
        // carry edition + edition_scope all the way through the bridge and
        // pipeline into the ChunkRequest the indexer receives — that is what
        // lets each indexed chunk self-declare its edition scope.
        var ctx = new TestContext();
        ctx.SeedExtractionAndChunking();

        var change = NewChange(
            documentId: "doc_x", contentHash: "hash-x",
            edition: "Pro", editionScope: "single-edition");

        await ctx.Handler.HandleAsync(change, CancellationToken.None);

        var call = Assert.Single(ctx.Indexer.Calls);
        Assert.Equal("Pro", call.Edition);
        Assert.Equal("single-edition", call.EditionScope);
    }

    [Fact]
    public async Task HandleAsync_HashUnchanged_FetchesBytesButPipelineStillShortCircuits()
    {
        // The pipeline takes the stream as a parameter, so the handler
        // fetches bytes BEFORE invoking the pipeline — even when the
        // pipeline will short-circuit on hash unchanged. Pinned as a
        // behavior contract because a future "lazy stream" optimization
        // (have the handler check the hash itself before fetching)
        // would need to update this test deliberately.
        var ctx = new TestContext();
        ctx.IndexState.SeedExistingHash("doc_x", "hash-x");

        var change = NewChange(documentId: "doc_x", contentHash: "hash-x");
        await ctx.Handler.HandleAsync(change, CancellationToken.None);

        // Indexer was NOT called (pipeline short-circuited).
        Assert.Empty(ctx.Indexer.Calls);
        // Bytes WERE fetched (handler doesn't know about the short-circuit).
        Assert.Single(ctx.BytesSource.Calls);
    }

    [Fact]
    public async Task HandleAsync_UnknownDocumentTypeString_FallsBackToOther()
    {
        // Defensive: a future schema addition that introduces a new
        // document type name shouldn't dead-letter every change until
        // the worker is rebuilt. ParseDocumentType maps unknown strings
        // to DocumentType.Other; the pipeline filters to Manual +
        // ServiceBulletin so Other documents get filtered (skipped),
        // not failed.
        Assert.Equal(DocumentType.Other,
            ScrapedDocumentChangeFeedHandler.ParseDocumentType("brand-new-type-from-future"));

        // Casing tolerance.
        Assert.Equal(DocumentType.Manual,
            ScrapedDocumentChangeFeedHandler.ParseDocumentType("manual"));
        Assert.Equal(DocumentType.ServiceBulletin,
            ScrapedDocumentChangeFeedHandler.ParseDocumentType("servicebulletin"));
    }

    [Fact]
    public async Task HandleAsync_NonAcceptedDocumentType_SkipsDownloadAndReturnTypeFiltered()
    {
        // Root cause of AB#259 backfill hang: Spooky software release blobs
        // (.beetlejuice firmware) were downloaded before the type filter fired.
        // The handler must short-circuit BEFORE fetching bytes for non-accepted types.
        var ctx = new TestContext();

        var change = new RagSourceDocument
        {
            Id = "doc_fw",
            DocumentId = "doc_fw",
            DocumentUrl = "https://example/doc_fw.bin",
            MachineId = TestMachineId,
            MachineTitle = "Beetlejuice",
            Manufacturer = "Spooky Pinball",
            DocumentType = "SoftwareRelease",
            ContentHash = "hash-fw",
        };
        var outcome = await ctx.Handler.HandleAsync(change, CancellationToken.None);

        Assert.Equal(IngestionOutcome.Skipped_DocumentTypeFiltered, outcome);
        Assert.Empty(ctx.BytesSource.Calls); // no download attempted
        Assert.Empty(ctx.Indexer.Calls);     // indexer not reached
    }

    [Fact]
    public async Task HandleAsync_NullChange_Throws()
    {
        var ctx = new TestContext();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ctx.Handler.HandleAsync(null!, CancellationToken.None));
    }

    // ── Task 7: blob-key threading tests ─────────────────────────────

    [Fact]
    public async Task HandleAsync_RawRecordHasBlobPath_PassesBlobNameToBytesSource()
    {
        // When the raw document record carries a File.LocalPath (= blob name),
        // the handler must pass that blob name (not the URL) to OpenAsync so
        // BlobDocumentBytesSource can serve from pinwiz-raw.
        var ctx = new TestContext();
        ctx.SeedExtractionAndChunking();

        const string blobName = "stern_manuals/Foo_Fighters_web.pdf";
        ctx.SeedRawRecord("doc_x", blobName);

        var change = NewChange(documentId: "doc_x", contentHash: "hash-x");
        await ctx.Handler.HandleAsync(change, CancellationToken.None);

        // The bytes source must have been called with the blob name, not the URL.
        Assert.Contains(blobName, ctx.BytesSource.Calls);
        Assert.DoesNotContain(change.DocumentUrl, ctx.BytesSource.Calls);
    }

    [Fact]
    public async Task HandleAsync_RawRecordAbsent_PassesDocumentUrlToBytesSource()
    {
        // When there is no raw record for the document (e.g. the document was
        // never downloaded — freshly scraped), the handler must fall back to
        // the original DocumentUrl so the HTTP source can perform a genuine fetch.
        var ctx = new TestContext();
        ctx.SeedExtractionAndChunking();
        // No raw record seeded — GetAsync returns null.

        var change = NewChange(documentId: "doc_y", contentHash: "hash-y");
        await ctx.Handler.HandleAsync(change, CancellationToken.None);

        Assert.Contains(change.DocumentUrl, ctx.BytesSource.Calls);
    }

    [Fact]
    public async Task HandleAsync_RawRecordExistsButFileIsNull_PassesDocumentUrlToBytesSource()
    {
        // When the raw record exists but File is null (record was discovered but
        // not yet downloaded), the handler must fall back to the DocumentUrl so
        // the HTTP source performs a live fetch — not a crash.
        var ctx = new TestContext();
        ctx.SeedExtractionAndChunking();
        ctx.SeedRawRecord("doc_z", blobName: null); // File present but LocalPath absent

        var change = NewChange(documentId: "doc_z", contentHash: "hash-z");
        await ctx.Handler.HandleAsync(change, CancellationToken.None);

        Assert.Contains(change.DocumentUrl, ctx.BytesSource.Calls);
    }

    // ────────────────────────────────────────────────────────────────
    // Test fixture
    // ────────────────────────────────────────────────────────────────

    private static RagSourceDocument NewChange(
        string documentId = "doc_default",
        string machineId = TestMachineId,
        string contentHash = "hash-default",
        string? edition = null,
        string? editionScope = null) => new()
    {
        Id = documentId,
        DocumentId = documentId,
        DocumentUrl = $"https://example/{documentId}.pdf",
        MachineId = machineId,
        MachineTitle = "Foo Fighters",
        Manufacturer = "Stern Pinball",
        DocumentType = "Manual",
        ContentHash = contentHash,
        Edition = edition,
        EditionScope = editionScope,
    };

    private sealed class TestContext
    {
        public InMemoryRagIndexer Indexer { get; } = new();
        public InMemoryIndexState IndexState { get; } = new();
        public InMemoryDocumentBytesSource BytesSource { get; } = new();
        public RecordingExtractor Extractor { get; } = new();
        public RecordingChunker Chunker { get; } = new();
        public ScrapedDocumentChangeFeedHandler Handler { get; }

        private readonly IRawDocumentRepository _rawRepo =
            Substitute.For<IRawDocumentRepository>();

        public TestContext()
        {
            var ingestionOptions = Options.Create(new RagIngestionOptions
            {
                AcceptedDocumentTypes = [DocumentType.Manual, DocumentType.ServiceBulletin],
                MaxFailuresPerDocument = 3,
            });
            var indexerOptions = Options.Create(new RagIndexerOptions());

            var pipeline = new ScrapedDocumentIngestionPipeline(
                Extractor,
                Chunker,
                Indexer,
                IndexState,
                ingestionOptions,
                indexerOptions,
                NullLogger<ScrapedDocumentIngestionPipeline>.Instance);

            Handler = new ScrapedDocumentChangeFeedHandler(
                pipeline,
                BytesSource,
                _rawRepo,
                ingestionOptions,
                NullLogger<ScrapedDocumentChangeFeedHandler>.Instance);
        }

        // Seeds the raw-document-repository mock to return a record with the
        // given blob name in File.LocalPath. Pass null blobName to seed a record
        // whose File property is null (document discovered but not yet downloaded).
        public void SeedRawRecord(string documentId, string? blobName)
        {
            var source = new SourceInfo
            {
                SourceType = SourceType.ManualsPage,
                DiscoveryUrl = $"https://example/{documentId}",
                DiscoveryContext = "Manuals Page",
                FileUrl = $"https://example/{documentId}.pdf",
            };
            var timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow };

            RawDocumentRecord record = new()
            {
                DocumentId = documentId,
                DocumentUrl = $"https://example/{documentId}.pdf",
                DocumentType = DocumentType.Manual,
                Source = source,
                Timeline = timeline,
                File = blobName is not null
                    ? new DownloadedFileInfo
                    {
                        LocalPath = blobName,
                        Filename = System.IO.Path.GetFileName(blobName),
                    }
                    : null,
            };

            _rawRepo
                .GetAsync(documentId, Arg.Any<CancellationToken>())
                .Returns(record);
        }

        public void SeedExtractionAndChunking()
        {
            Extractor.NextResult = new ExtractedDocument(
                Status: ExtractionStatus.Success,
                Text: "page 1",
                Pages: [new ExtractedPage(1, "page 1")],
                Outline: [],
                Error: null);
            Chunker.NextChunks =
            [
                new Chunk(0, "chunk 0", "Section", PageStart: 1, PageEnd: 1, TokenCount: 50),
            ];
        }
    }

    private sealed class RecordingExtractor : IDocumentTextExtractor
    {
        public ExtractedDocument NextResult { get; set; } = ExtractedDocument.Failure(
            ExtractionStatus.Malformed,
            "no result configured");

        public Task<ExtractedDocument> ExtractAsync(Stream pdfStream, CancellationToken cancellationToken) =>
            Task.FromResult(NextResult);
    }

    private sealed class RecordingChunker : IChunker
    {
        public IReadOnlyList<Chunk> NextChunks { get; set; } = [];

        public IReadOnlyList<Chunk> Chunk(
            ExtractedDocument document,
            ChunkRequest request,
            CancellationToken cancellationToken) =>
            NextChunks;
    }
}
