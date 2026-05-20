using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Ingestion;

// Integration tests for CosmosChangeFeedHostedService<T>. Drives the
// HandleChangesAsync entry point directly (no Cosmos emulator) against
// in-memory fakes to assert the dead-letter / short-circuit / batch-
// advance contracts the W3-2 design specifies.
//
// The Container ctor params on the hosted service are NSubstitute
// fakes — they're never actually called (HandleChangesAsync doesn't
// touch Cosmos) and exist only because the SUT's ctor demands them.
public sealed class CosmosChangeFeedHostedServiceTests
{
    private const string DocumentIdA = "doc_a";
    private const string DocumentIdB = "doc_b";

    [Fact]
    public async Task HandleChangesAsync_HappyPath_InvokesHandlerForEachChange()
    {
        var ctx = new TestContext();
        var changes = new[] { NewChange(DocumentIdA), NewChange(DocumentIdB) };

        await ctx.Service.HandleChangesAsync(changes, CancellationToken.None);

        Assert.Equal(2, ctx.Handler.Invocations.Count);
        Assert.Contains(DocumentIdA, ctx.Handler.Invocations);
        Assert.Contains(DocumentIdB, ctx.Handler.Invocations);
        Assert.Empty(ctx.DeadLetterSink.Snapshot);
    }

    [Fact]
    public async Task HandleChangesAsync_HandlerThrows_DeadLettersAtAttempt1()
    {
        var ctx = new TestContext();
        ctx.Handler.ThrowFor.Add(DocumentIdA);

        var changes = new[] { NewChange(DocumentIdA) };
        await ctx.Service.HandleChangesAsync(changes, CancellationToken.None);

        var dl = Assert.Contains(DocumentIdA, ctx.DeadLetterSink.Snapshot);
        Assert.Equal(1, dl.AttemptCount);
        Assert.Equal(nameof(InvalidOperationException), dl.ErrorClass);
    }

    [Fact]
    public async Task HandleChangesAsync_RepeatedFailure_IncrementsAttemptCount()
    {
        // Three deliveries of the same poison change should advance the
        // dead-letter row's AttemptCount each time, not start over at 1.
        // Once AttemptCount reaches MaxFailuresPerDocument the next
        // delivery is short-circuited (the handler is never invoked).
        var ctx = new TestContext(maxFailuresPerDocument: 3);
        ctx.Handler.ThrowFor.Add(DocumentIdA);

        for (int i = 0; i < 4; i++)
        {
            await ctx.Service.HandleChangesAsync([NewChange(DocumentIdA)], CancellationToken.None);
        }

        // 1st + 2nd + 3rd deliveries = 3 invocations, then the 4th
        // short-circuits before invoking the handler.
        Assert.Equal(3, ctx.Handler.Invocations.Count);
        var dl = Assert.Contains(DocumentIdA, ctx.DeadLetterSink.Snapshot);
        Assert.Equal(3, dl.AttemptCount);
    }

    [Fact]
    public async Task HandleChangesAsync_PriorDeadLetterAtBudget_SkipsHandlerEntirely()
    {
        var ctx = new TestContext(maxFailuresPerDocument: 3);
        ctx.DeadLetterSink.SeedExisting(new DeadLetterRecord(
            DocumentId: DocumentIdA,
            AttemptCount: 3,
            LastAttemptUtc: DateTimeOffset.UtcNow,
            ErrorClass: "Old",
            ErrorMessage: "old failure",
            ChangeLsn: null));

        await ctx.Service.HandleChangesAsync([NewChange(DocumentIdA)], CancellationToken.None);

        Assert.Empty(ctx.Handler.Invocations);
        // AttemptCount should not have advanced — handler was never called.
        Assert.Equal(3, ctx.DeadLetterSink.Snapshot[DocumentIdA].AttemptCount);
    }

    [Fact]
    public async Task HandleChangesAsync_OneDocumentFails_BatchStillAdvances()
    {
        // Critical lease-progression contract: a single poison document
        // must NOT prevent the rest of the batch from being handled. The
        // Change Feed checkpoint advances when HandleChangesAsync returns
        // without throwing; throwing on document A would leave document B
        // unprocessed AND re-run document A on the next delivery cycle.
        var ctx = new TestContext();
        ctx.Handler.ThrowFor.Add(DocumentIdA);

        var changes = new[] { NewChange(DocumentIdA), NewChange(DocumentIdB) };
        await ctx.Service.HandleChangesAsync(changes, CancellationToken.None);

        // doc_b made it through despite doc_a throwing.
        Assert.Contains(DocumentIdB, ctx.Handler.Invocations);
        Assert.Contains(DocumentIdA, ctx.DeadLetterSink.Snapshot);
        Assert.DoesNotContain(DocumentIdB, ctx.DeadLetterSink.Snapshot);
    }

    [Fact]
    public async Task HandleChangesAsync_EmptyDocumentId_SkipsWithoutInvokingHandler()
    {
        // An incoming change with no DocumentId is a malformed source
        // record; the dead-letter container is keyed by document_id so we
        // can't usefully record it there. Skip + warn-log; batch advances.
        var ctx = new TestContext();
        var change = new RagSourceDocument
        {
            Id = "x",
            DocumentId = "",
            DocumentUrl = "https://example/foo.pdf",
            MachineId = "GRBN-MQR4P",
            MachineTitle = "Foo Fighters",
            Manufacturer = "Stern Pinball",
            DocumentType = "Manual",
            ContentHash = "h",
        };

        await ctx.Service.HandleChangesAsync([change], CancellationToken.None);

        Assert.Empty(ctx.Handler.Invocations);
        Assert.Empty(ctx.DeadLetterSink.Snapshot);
    }

    [Fact]
    public async Task HandleChangesAsync_HostCancellation_PropagatesAndAbortsBatch()
    {
        // A cancellation tied to host shutdown must propagate so the
        // BackgroundService loop unwinds cleanly. The cancellation must
        // fire mid-batch (between documents) so we exercise the
        // ThrowIfCancellationRequested at the loop boundary.
        var ctx = new TestContext();
        using var cts = new CancellationTokenSource();
        ctx.Handler.OnInvoke = _ => cts.Cancel();

        var changes = new[] { NewChange(DocumentIdA), NewChange(DocumentIdB) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.Service.HandleChangesAsync(changes, cts.Token));

        // Document A's handler ran (and triggered the cancellation);
        // document B's handler must not.
        Assert.Single(ctx.Handler.Invocations);
        Assert.Equal(DocumentIdA, ctx.Handler.Invocations[0]);
    }

    [Fact]
    public async Task HandleChangesAsync_LongErrorMessage_TruncatedTo1024()
    {
        var ctx = new TestContext();
        var longMessage = new string('x', 2000);
        ctx.Handler.ThrowOverride = _ => throw new InvalidOperationException(longMessage);

        await ctx.Service.HandleChangesAsync([NewChange(DocumentIdA)], CancellationToken.None);

        var dl = Assert.Contains(DocumentIdA, ctx.DeadLetterSink.Snapshot);
        Assert.Equal(1024, dl.ErrorMessage.Length);
    }

    [Fact]
    public async Task HandleChangesAsync_RecordsChangeLsnFromPayload()
    {
        var ctx = new TestContext();
        ctx.Handler.ThrowFor.Add(DocumentIdA);
        var change = NewChange(DocumentIdA, lsn: 987L);

        await ctx.Service.HandleChangesAsync([change], CancellationToken.None);

        var dl = Assert.Contains(DocumentIdA, ctx.DeadLetterSink.Snapshot);
        Assert.Equal("987", dl.ChangeLsn);
    }

    // ────────────────────────────────────────────────────────────────
    // Test fixture
    // ────────────────────────────────────────────────────────────────

    private static RagSourceDocument NewChange(string documentId, long? lsn = null) => new()
    {
        Id = documentId,
        DocumentId = documentId,
        DocumentUrl = $"https://example/{documentId}.pdf",
        MachineId = "GRBN-MQR4P",
        MachineTitle = "Foo Fighters",
        Manufacturer = "Stern Pinball",
        DocumentType = "Manual",
        ContentHash = "hash-default",
        Lsn = lsn,
    };

    private sealed class RecordingHandler : ICosmosChangeFeedHandler<RagSourceDocument>
    {
        public List<string> Invocations { get; } = [];
        public HashSet<string> ThrowFor { get; } = [];
        public Action<RagSourceDocument>? OnInvoke { get; set; }
        public Action<RagSourceDocument>? ThrowOverride { get; set; }

        public Task<IngestionOutcome?> HandleAsync(RagSourceDocument change, CancellationToken cancellationToken)
        {
            Invocations.Add(change.DocumentId);
            OnInvoke?.Invoke(change);
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOverride is not null)
            {
                ThrowOverride(change);
            }
            if (ThrowFor.Contains(change.DocumentId))
            {
                throw new InvalidOperationException($"simulated handler failure for {change.DocumentId}");
            }
            return Task.FromResult<IngestionOutcome?>(null);
        }
    }

    private sealed class TestContext
    {
        public RecordingHandler Handler { get; } = new();
        public InMemoryDeadLetterSink DeadLetterSink { get; } = new();
        public CosmosChangeFeedHostedService<RagSourceDocument> Service { get; }

        public TestContext(int maxFailuresPerDocument = 3)
        {
            var ingestionOptions = Options.Create(new RagIngestionOptions
            {
                CuratedSubsetMachineIds = ["GRBN-MQR4P"],
                AcceptedDocumentTypes = [Core.Models.DocumentType.Manual, Core.Models.DocumentType.ServiceBulletin],
                MaxFailuresPerDocument = maxFailuresPerDocument,
            });
            var changeFeedOptions = Options.Create(new CosmosChangeFeedHostedServiceOptions
            {
                InstanceName = "test-instance",
            });

            // The hosted service ctor demands Container instances even
            // though HandleChangesAsync never touches them. NSubstitute
            // fakes satisfy the ctor without doing any wiring.
            var sourceContainer = Substitute.For<Container>();
            var leaseContainer = Substitute.For<Container>();

            Service = new CosmosChangeFeedHostedService<RagSourceDocument>(
                sourceContainer,
                leaseContainer,
                Handler,
                DeadLetterSink,
                static d => d.DocumentId,
                static d => d.Lsn?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ingestionOptions,
                changeFeedOptions,
                TimeProvider.System,
                NullLogger<CosmosChangeFeedHostedService<RagSourceDocument>>.Instance);
        }
    }
}
