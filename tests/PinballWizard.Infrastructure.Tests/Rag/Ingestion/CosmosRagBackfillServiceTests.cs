using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Ingestion;

// Behavioral tests for CosmosRagBackfillService. Drives RunAsync with
// NSubstitute fakes for the Cosmos Container + FeedIterator so the
// iterator loop, exception isolation, cancellation propagation, and
// outcome counting can be asserted without a real Cosmos endpoint.
//
// Page JSON shape: { "Documents": [ { "id": "x", "document_id": "x", ... } ] }
// matches what CosmosRagBackfillService.ChangeFeedPage expects.
public sealed class CosmosRagBackfillServiceTests
{
    private const string TestMachineId = "GRBN-MQR4P";
    private const string DocA = "doc_a";
    private const string DocB = "doc_b";

    [Fact]
    public async Task RunAsync_HappyPath_ProcessesAllDocuments()
    {
        var ctx = new TestContext();
        ctx.Iterator.SetPages([
            [NewDoc(DocA), NewDoc(DocB)]
        ]);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(2, result.Indexed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunAsync_MultiplePages_ProcessesAllPages()
    {
        var ctx = new TestContext();
        ctx.Iterator.SetPages([
            [NewDoc(DocA)],
            [NewDoc(DocB)]
        ]);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(2, result.Indexed);
    }

    [Fact]
    public async Task RunAsync_HandlerThrows_DocumentCountedAsFailed_OtherDocumentsContinue()
    {
        // Critical blast-radius contract: a handler exception for doc A
        // must not prevent doc B from being processed. Failed counter
        // increments; processed + indexed reflect the rest.
        var ctx = new TestContext();
        ctx.Handler.ThrowFor.Add(DocA);
        ctx.Iterator.SetPages([
            [NewDoc(DocA), NewDoc(DocB)]
        ]);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Indexed);   // doc_b only
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.Failed);    // doc_a
    }

    [Fact]
    public async Task RunAsync_HandlerReturnsSkippedOutcome_CountedAsSkipped()
    {
        // Documents filtered by the pipeline (type filter, hash short-circuit,
        // etc.) return a Skipped_* outcome. The backfill service must NOT
        // count these as indexed.
        var ctx = new TestContext();
        ctx.Handler.OutcomeOverride = IngestionOutcome.Skipped_DocumentTypeFiltered;
        ctx.Iterator.SetPages([
            [NewDoc(DocA), NewDoc(DocB)]
        ]);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunAsync_CancellationMidBatch_PropagatesAndAbortsRun()
    {
        // A cancellation token fired by the host while the batch is
        // in-flight must propagate so the CLI process exits cleanly.
        var ctx = new TestContext();
        using var cts = new CancellationTokenSource();
        ctx.Handler.OnInvoke = _ => cts.Cancel();
        ctx.Iterator.SetPages([
            [NewDoc(DocA), NewDoc(DocB)]
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.Service.RunAsync(cts.Token));

        // doc_a handler ran (triggering cancel); doc_b must not.
        Assert.Single(ctx.Handler.Invocations);
    }

    [Fact]
    public async Task RunAsync_NonSuccessStatusCode_StopsIteration()
    {
        // A non-2xx change-feed response signals a service error.
        // The service must stop iterating rather than spin forever.
        var ctx = new TestContext();
        ctx.Iterator.SetPages(
            [[NewDoc(DocA)]],
            failOnNextPage: true);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        // First page (with doc_a) succeeded; second read returns non-2xx
        // which halts the loop. doc_a was processed; nothing more.
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunAsync_EmptyPages_SkippedGracefully()
    {
        // The iterator may emit empty pages (NotModified / tail-reached).
        // Nothing should be counted as processed or failed.
        var ctx = new TestContext();
        ctx.Iterator.SetPages([
            []  // empty page — no documents
        ]);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunAsync_AllDocumentsFail_ResultReflectsAllAsFailed()
    {
        var ctx = new TestContext();
        ctx.Handler.ThrowFor.Add(DocA);
        ctx.Handler.ThrowFor.Add(DocB);
        ctx.Iterator.SetPages([
            [NewDoc(DocA), NewDoc(DocB)]
        ]);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2, result.Failed);
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static RagSourceDocument NewDoc(string documentId) => new()
    {
        Id = documentId,
        DocumentId = documentId,
        DocumentUrl = $"https://example/{documentId}.pdf",
        MachineId = TestMachineId,
        MachineTitle = "Foo Fighters",
        Manufacturer = "Stern Pinball",
        DocumentType = "Manual",
        ContentHash = $"hash-{documentId}",
    };

    private static ResponseMessage MakePage(IReadOnlyList<RagSourceDocument> docs)
    {
        var json = JsonSerializer.Serialize(new { Documents = docs });
        var msg = new ResponseMessage(HttpStatusCode.OK, requestMessage: null!, errorMessage: null!);
        msg.Content = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return msg;
    }

    private static ResponseMessage MakeNotModified() =>
        new ResponseMessage(HttpStatusCode.NotModified, requestMessage: null!, errorMessage: null!);

    private static ResponseMessage MakeError() =>
        new ResponseMessage(HttpStatusCode.InternalServerError, requestMessage: null!, errorMessage: null!);

    // ────────────────────────────────────────────────────────────────
    // Fakes
    // ────────────────────────────────────────────────────────────────

    private sealed class RecordingHandler : ICosmosChangeFeedHandler<RagSourceDocument>
    {
        public List<string> Invocations { get; } = [];
        public HashSet<string> ThrowFor { get; } = [];
        public Action<RagSourceDocument>? OnInvoke { get; set; }
        public IngestionOutcome? OutcomeOverride { get; set; }

        public Task<IngestionOutcome?> HandleAsync(RagSourceDocument change, CancellationToken cancellationToken)
        {
            Invocations.Add(change.DocumentId);
            OnInvoke?.Invoke(change);
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowFor.Contains(change.DocumentId))
                throw new InvalidOperationException($"simulated failure for {change.DocumentId}");
            var outcome = OutcomeOverride ?? IngestionOutcome.Indexed;
            return Task.FromResult<IngestionOutcome?>(outcome);
        }
    }

    // Fake FeedIterator that drains a pre-configured list of pages.
    // Each page is either a success ResponseMessage or an error.
    // `failOnNextPage` causes the iterator to return a non-2xx response
    // after the supplied pages have been consumed.
    private sealed class FakeFeedIterator : FeedIterator
    {
        private readonly Queue<ResponseMessage> _pages = new();
        private bool _failOnNextPage;

        public override bool HasMoreResults =>
            _pages.Count > 0 || _failOnNextPage;

        public void SetPages(
            IReadOnlyList<IReadOnlyList<RagSourceDocument>> pages,
            bool failOnNextPage = false)
        {
            foreach (var p in pages)
                _pages.Enqueue(MakePage(p));
            _failOnNextPage = failOnNextPage;
        }

        public override Task<ResponseMessage> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pages.Count > 0)
                return Task.FromResult(_pages.Dequeue());
            if (_failOnNextPage)
            {
                _failOnNextPage = false;
                return Task.FromResult(MakeError());
            }
            return Task.FromResult(MakeNotModified());
        }
    }

    private sealed class TestContext
    {
        public RecordingHandler Handler { get; } = new();
        public FakeFeedIterator Iterator { get; } = new();
        public CosmosRagBackfillService Service { get; }

        public TestContext()
        {
            var options = Options.Create(new RagIngestionOptions
            {
                AcceptedDocumentTypes = [Core.Models.DocumentType.Manual, Core.Models.DocumentType.ServiceBulletin],
            });

            var sourceContainer = Substitute.For<Container>();
            sourceContainer
                .GetChangeFeedStreamIterator(
                    Arg.Any<ChangeFeedStartFrom>(),
                    Arg.Any<ChangeFeedMode>(),
                    Arg.Any<ChangeFeedRequestOptions>())
                .Returns(Iterator);

            Service = new CosmosRagBackfillService(
                sourceContainer,
                Handler,
                options,
                NullLogger<CosmosRagBackfillService>.Instance);
        }
    }
}
