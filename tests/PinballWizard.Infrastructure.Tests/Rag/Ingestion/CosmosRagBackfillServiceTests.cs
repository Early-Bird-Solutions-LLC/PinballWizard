using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

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
        // BackfillConcurrency=1 makes this deterministic: doc_a runs,
        // fires cancel, then doc_b's gate.WaitAsync throws
        // OperationCanceledException before the handler can be invoked.
        var ctx = new TestContext(backfillConcurrency: 1);
        using var cts = new CancellationTokenSource();
        ctx.Handler.OnInvoke = _ => cts.Cancel();
        ctx.Iterator.SetPages([
            [NewDoc(DocA), NewDoc(DocB)]
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.Service.RunAsync(cts.Token));

        // With concurrency=1, only doc_a's handler can have run — doc_b
        // is still waiting on the semaphore when the token is cancelled.
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

    [Fact]
    public async Task RunAsync_FeedDrainedWith304_CompletesSuccessfully_NoErrorLogged()
    {
        // The Cosmos change-feed pull-model stream iterator signals "fully
        // drained" by returning HTTP 304 NotModified. HasMoreResults stays
        // true for the lifetime of the iterator (it is a live stream), so
        // the 304 is the only way the SDK can indicate a caught-up feed.
        // The service must treat this as normal completion — not an error —
        // so operators don't receive false-alarm "re-run required" alerts.
        var logger = new CapturingLogger<CosmosRagBackfillService>();
        var ctx = new TestContext(logger: logger);
        ctx.Iterator.SetPages(
            [[NewDoc(DocA)]],
            drain304OnNextPage: true);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        // doc_a was processed from the first page before the 304 arrived.
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);

        // 304 must not emit any Error-level log entry.
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task RunAsync_GenuineServerError500_LogsError()
    {
        // A non-2xx status that is NOT 304 signals a real service error and
        // must still log at Error level so operators know the backfill is
        // incomplete and a re-run is required.
        var logger = new CapturingLogger<CosmosRagBackfillService>();
        var ctx = new TestContext(logger: logger);
        ctx.Iterator.SetPages(
            [[NewDoc(DocA)]],
            failOnNextPage: true);

        var result = await ctx.Service.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
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
    // `failOnNextPage` causes the iterator to return a non-2xx (500) response
    // after the supplied pages have been consumed.
    // `drain304OnNextPage` causes the iterator to return 304 NotModified after
    // the supplied pages have been consumed, with HasMoreResults staying true
    // until the 304 is consumed — matching the real Cosmos SDK's drain signal.
    private sealed class FakeFeedIterator : FeedIterator
    {
        private readonly Queue<ResponseMessage> _pages = new();
        private bool _failOnNextPage;
        private bool _drain304OnNextPage;

        public override bool HasMoreResults =>
            _pages.Count > 0 || _failOnNextPage || _drain304OnNextPage;

        public void SetPages(
            IReadOnlyList<IReadOnlyList<RagSourceDocument>> pages,
            bool failOnNextPage = false,
            bool drain304OnNextPage = false)
        {
            foreach (var p in pages)
                _pages.Enqueue(MakePage(p));
            _failOnNextPage = failOnNextPage;
            _drain304OnNextPage = drain304OnNextPage;
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
            if (_drain304OnNextPage)
            {
                _drain304OnNextPage = false;
                return Task.FromResult(MakeNotModified());
            }
            return Task.FromResult(MakeNotModified());
        }
    }

    private sealed class TestContext
    {
        public RecordingHandler Handler { get; } = new();
        public FakeFeedIterator Iterator { get; } = new();
        public CosmosRagBackfillService Service { get; }

        public TestContext(
            int backfillConcurrency = 4,
            ILogger<CosmosRagBackfillService>? logger = null)
        {
            var options = Options.Create(new RagIngestionOptions
            {
                AcceptedDocumentTypes = [Core.Models.DocumentType.Manual, Core.Models.DocumentType.ServiceBulletin],
                BackfillConcurrency = backfillConcurrency,
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
                logger ?? NullLogger<CosmosRagBackfillService>.Instance);
        }
    }

    // Minimal capturing logger for asserting log level and message content.
    // Thread-safe: xUnit may run test classes in parallel but each test
    // constructs its own instance.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message);
}
