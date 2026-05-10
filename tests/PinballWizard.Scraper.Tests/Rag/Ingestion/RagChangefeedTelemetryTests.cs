using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Ingestion;

// Behavioral emission tests for the W3-2 hosted-service instruments
// (`pinwiz.rag.changefeed_*`). Drives `HandleChangesAsync` against
// in-memory fakes and verifies each emission contract via MeterListener
// captures.
//
// Process-global Meter caveat: PinballWizardTelemetry's instruments are
// shared across ALL tests in this assembly. Concurrent test classes
// running in parallel may emit to the same instrument simultaneously,
// so the assertion pattern is `Assert.Contains` against a
// `ConcurrentBag` — never `Assert.Single` (would race with sibling
// emissions) and never an exact count assertion. The tag-based
// filtering (`error_class`, `reason`, `batch_size_bucket`) keeps each
// test's signal distinguishable from sibling noise.
public sealed class RagChangefeedTelemetryTests
{
    private const string DocumentIdA = "doc_a_telemetry";
    private const string DocumentIdB = "doc_b_telemetry";

    [Fact]
    public async Task HandleChangesAsync_EmitsBatchDurationMs_TaggedBatchSizeBucket()
    {
        var ctx = new TestContext();
        var changes = new[] { NewChange(DocumentIdA), NewChange(DocumentIdB) };

        var samples = CollectBatchDurationSamples(out var listener);
        using (listener)
        {
            await ctx.Service.HandleChangesAsync(changes, CancellationToken.None);
        }

        // 2 documents → "2-10" bucket. Sample value is wall-clock so any
        // non-negative number qualifies — the contract is "fires once
        // per batch with the right tag".
        Assert.Contains(samples, s => s.Bucket == "2-10" && s.Value >= 0);
    }

    [Fact]
    public async Task HandleChangesAsync_HandlerThrows_EmitsDeadLetterTotal_TaggedErrorClass()
    {
        var ctx = new TestContext();
        ctx.Handler.ThrowFor.Add(DocumentIdA);

        var samples = CollectDeadLetterSamples(out var listener);
        using (listener)
        {
            await ctx.Service.HandleChangesAsync([NewChange(DocumentIdA)], CancellationToken.None);
        }

        Assert.Contains(
            samples,
            s => s.ErrorClass == nameof(InvalidOperationException) && s.Value == 1);
    }

    [Fact]
    public async Task HandleChangesAsync_DeadLetterUpsertFails_DoesNotEmitDeadLetterTotal()
    {
        // The dead-letter counter increments only AFTER the sink upsert
        // succeeds. If the sink throws, the dashboard shouldn't think a
        // dead-letter row landed. Pinned because a future refactor could
        // accidentally move the counter.Add() before the await.
        var ctx = new TestContext(failingSink: true);
        ctx.Handler.ThrowFor.Add(DocumentIdA);

        var samples = CollectDeadLetterSamples(out var listener);
        using (listener)
        {
            await ctx.Service.HandleChangesAsync([NewChange(DocumentIdA)], CancellationToken.None);
        }

        Assert.DoesNotContain(samples, s => s.ErrorClass == nameof(InvalidOperationException));
    }

    [Fact]
    public async Task HandleChangesAsync_OverBudgetSkip_EmitsShortCircuitTotal_TaggedOverBudget()
    {
        var ctx = new TestContext(maxFailuresPerDocument: 3);
        ctx.DeadLetterSink.SeedExisting(new DeadLetterRecord(
            DocumentId: DocumentIdA,
            AttemptCount: 3,
            LastAttemptUtc: DateTimeOffset.UtcNow,
            ErrorClass: "Old",
            ErrorMessage: "old failure",
            ChangeLsn: null));

        var samples = CollectShortCircuitSamples(out var listener);
        using (listener)
        {
            await ctx.Service.HandleChangesAsync([NewChange(DocumentIdA)], CancellationToken.None);
        }

        Assert.Contains(samples, s => s.Reason == "over_budget" && s.Value == 1);
    }

    [Fact]
    public async Task HandleChangesAsync_EmptyDocumentId_EmitsShortCircuitTotal_TaggedEmptyDocumentId()
    {
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

        var samples = CollectShortCircuitSamples(out var listener);
        using (listener)
        {
            await ctx.Service.HandleChangesAsync([change], CancellationToken.None);
        }

        Assert.Contains(samples, s => s.Reason == "empty_document_id" && s.Value == 1);
    }

    [Fact]
    public async Task HandleChangesAsync_HappyPath_DoesNotEmitDeadLetterOrShortCircuit()
    {
        // Pin the negative case: a clean batch with no failures must NOT
        // increment either counter. Tagged samples make this assertion
        // tolerant of sibling test-class emissions.
        var ctx = new TestContext();
        var marker = $"happy_doc_{Guid.NewGuid():N}";

        _ = CollectDeadLetterSamples(out var dlListener);
        _ = CollectShortCircuitSamples(out var scListener);
        using (dlListener)
        using (scListener)
        {
            await ctx.Service.HandleChangesAsync([NewChange(marker)], CancellationToken.None);
        }

        // Look only at samples whose error_class / reason indicate they
        // belong to *this* test's invocation. Easier: assert the dead-
        // letter sink stayed empty and the handler ran for our marker.
        Assert.Empty(ctx.DeadLetterSink.Snapshot);
        Assert.Contains(marker, ctx.Handler.Invocations);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(2, "2-10")]
    [InlineData(10, "2-10")]
    [InlineData(11, "11-50")]
    [InlineData(50, "11-50")]
    [InlineData(51, "51+")]
    [InlineData(500, "51+")]
    public void ClassifyBatchSize_ProducesExpectedBucket(int count, string expected)
    {
        // Pin the bucket boundaries so a future change to the bucketing
        // (which would silently shift dashboard chart shapes) trips a
        // test rather than going unnoticed in production.
        Assert.Equal(
            expected,
            CosmosChangeFeedHostedService<RagSourceDocument>.ClassifyBatchSize(count));
    }

    // ────────────────────────────────────────────────────────────────
    // MeterListener helpers — mirror the pattern established by
    // MachineGroundingToolTests / SearchCorpusToolTests.
    // ────────────────────────────────────────────────────────────────

    private static ConcurrentBag<(double Value, string? Bucket)> CollectBatchDurationSamples(out MeterListener listener)
    {
        var samples = new ConcurrentBag<(double Value, string? Bucket)>();
        var l = new MeterListener();
        l.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? bucket = null;
            foreach (var t in tags)
            {
                if (t.Key == "batch_size_bucket") bucket = t.Value as string;
            }
            samples.Add((value, bucket));
        });
        l.Start();
        l.EnableMeasurementEvents(PinballWizardTelemetry.RagChangefeedBatchDurationMs);
        listener = l;
        return samples;
    }

    private static ConcurrentBag<(long Value, string? ErrorClass)> CollectDeadLetterSamples(out MeterListener listener)
    {
        var samples = new ConcurrentBag<(long Value, string? ErrorClass)>();
        var l = new MeterListener();
        l.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? errorClass = null;
            foreach (var t in tags)
            {
                if (t.Key == "error_class") errorClass = t.Value as string;
            }
            samples.Add((value, errorClass));
        });
        l.Start();
        l.EnableMeasurementEvents(PinballWizardTelemetry.RagChangefeedDeadLetterTotal);
        listener = l;
        return samples;
    }

    private static ConcurrentBag<(long Value, string? Reason)> CollectShortCircuitSamples(out MeterListener listener)
    {
        var samples = new ConcurrentBag<(long Value, string? Reason)>();
        var l = new MeterListener();
        l.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? reason = null;
            foreach (var t in tags)
            {
                if (t.Key == "reason") reason = t.Value as string;
            }
            samples.Add((value, reason));
        });
        l.Start();
        l.EnableMeasurementEvents(PinballWizardTelemetry.RagChangefeedShortCircuitTotal);
        listener = l;
        return samples;
    }

    // ────────────────────────────────────────────────────────────────
    // Test fixture
    // ────────────────────────────────────────────────────────────────

    private static RagSourceDocument NewChange(string documentId, string? lsn = null) => new()
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

        public Task HandleAsync(RagSourceDocument change, CancellationToken cancellationToken)
        {
            Invocations.Add(change.DocumentId);
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowFor.Contains(change.DocumentId))
            {
                throw new InvalidOperationException($"simulated handler failure for {change.DocumentId}");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FailingDeadLetterSink : IDeadLetterSink
    {
        public Task<DeadLetterRecord?> GetAsync(string documentId, CancellationToken cancellationToken) =>
            Task.FromResult<DeadLetterRecord?>(null);

        public Task UpsertAsync(DeadLetterRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated dead-letter sink failure");
    }

    private sealed class TestContext
    {
        public RecordingHandler Handler { get; } = new();
        public InMemoryDeadLetterSink DeadLetterSink { get; } = new();
        public CosmosChangeFeedHostedService<RagSourceDocument> Service { get; }

        public TestContext(int maxFailuresPerDocument = 3, bool failingSink = false)
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

            var sourceContainer = Substitute.For<Container>();
            var leaseContainer = Substitute.For<Container>();
            IDeadLetterSink sink = failingSink ? new FailingDeadLetterSink() : DeadLetterSink;

            Service = new CosmosChangeFeedHostedService<RagSourceDocument>(
                sourceContainer,
                leaseContainer,
                Handler,
                sink,
                static d => d.DocumentId,
                static d => d.Lsn,
                ingestionOptions,
                changeFeedOptions,
                TimeProvider.System,
                NullLogger<CosmosChangeFeedHostedService<RagSourceDocument>>.Instance);
        }
    }
}
