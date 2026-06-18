using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

/// <summary>
/// Verifies the inline boundary instrumentation added to
/// <see cref="CosmosRepository{T}"/> in PR 4 of the Cosmos for User
/// Delight track per ADR-0025 § 8: every SDK call records an
/// observation on <c>pinwiz.cosmos.ru_charge</c> and
/// <c>pinwiz.cosmos.query_duration_ms</c> tagged with the container
/// name and the operation type, and a non-404
/// <see cref="CosmosException"/> captures
/// <see cref="CosmosException.Diagnostics"/> into a structured log
/// scope before rethrowing.
/// </summary>
/// <remarks>
/// MeterListener pattern follows the project convention documented in
/// <c>memory/project_meterlistener_test_pattern.md</c>: <see cref="ConcurrentBag{T}"/>
/// + tag-predicate filtering rather than <c>Assert.Single</c>, because
/// sibling test classes emitting on the same process-global Meter race
/// concurrently with this fixture.
/// </remarks>
public sealed class CosmosRepositoryMetricsTests
{
    // Distinct container Id per fixture — used as the `container` tag
    // value so this class's emissions are reliably distinguishable from
    // sibling classes' emissions on the shared Meter.
    private const string ContainerId = "metrics-test-container";

    // Tolerance-based equality avoids the cs/equality-on-floats finding
    // even though, in practice, no arithmetic happens on the RU values
    // between fixture and assertion (they flow as exact bits).
    private static bool RuEquals(double actual, double expected) =>
        Math.Abs(actual - expected) < 1e-9;

    [Fact]
    public async Task GetByIdAsync_Success_EmitsRuChargeAndDuration()
    {
        var (repo, container, _) = NewRepository();
        var entity = new TestEntity { Id = "x", PartitionKey = "p", Name = "n" };
        var response = MakeItemResponse(entity, HttpStatusCode.OK, requestCharge: 1.5);
        container
            .ReadItemAsync<TestEntity>("x", new PartitionKey("p"), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        using var listener = CollectCosmosMetrics(out var ru, out var duration);

        await repo.GetByIdAsync("x", "p", CancellationToken.None);

        // Tag-filtered Assert.Contains because parallel test classes can
        // emit on the same instruments concurrently.
        Assert.Contains(ru, s => s.Container == ContainerId && s.Operation == "read" && RuEquals(s.Value, 1.5));
        Assert.Contains(duration, s => s.Container == ContainerId && s.Operation == "read" && s.Value >= 0);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_StillEmitsMetrics()
    {
        // 404 is normal flow but the RU + duration are still useful
        // operational signal — operators want to see the cost of
        // looking for a missing item, not have it silently disappear.
        var (repo, container, _) = NewRepository();
        container
            .ReadItemAsync<TestEntity>("missing", new PartitionKey("p"), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(MakeCosmosException(HttpStatusCode.NotFound, requestCharge: 1.0));

        using var listener = CollectCosmosMetrics(out var ru, out var duration);

        var result = await repo.GetByIdAsync("missing", "p", CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(ru, s => s.Container == ContainerId && s.Operation == "read" && RuEquals(s.Value, 1.0));
        Assert.Contains(duration, s => s.Container == ContainerId && s.Operation == "read");
    }

    [Fact]
    public async Task GetByIdAsync_TooManyRequests_EmitsExceptionRequestCharge()
    {
        // 429 is the canonical "operator should care" failure — RU was
        // burned even though no document came back. Pin that the
        // metrics emit `CosmosException.RequestCharge` on this path.
        var (repo, container, _) = NewRepository();
        container
            .ReadItemAsync<TestEntity>("hot", new PartitionKey("p"), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(MakeCosmosException(HttpStatusCode.TooManyRequests, requestCharge: 7.25));

        using var listener = CollectCosmosMetrics(out var ru, out _);

        await Assert.ThrowsAsync<CosmosException>(() =>
            repo.GetByIdAsync("hot", "p", CancellationToken.None));

        Assert.Contains(ru, s => s.Container == ContainerId && s.Operation == "read" && RuEquals(s.Value, 7.25));
    }

    [Fact]
    public async Task GetByIdAsync_TooManyRequests_CapturesDiagnosticsInLogScope()
    {
        // The Diagnostics surface (region, retry count, timing breakdown)
        // is the load-bearing operator signal when a 429/503/408 fires.
        // Pin that it ends up in the structured log scope so an App
        // Insights query lands on the failure context without a
        // separate trace lookup.
        var (repo, container, capturingLogger) = NewRepository();
        container
            .ReadItemAsync<TestEntity>("hot", new PartitionKey("p"), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(MakeCosmosException(HttpStatusCode.TooManyRequests, requestCharge: 7.25));

        await Assert.ThrowsAsync<CosmosException>(() =>
            repo.GetByIdAsync("hot", "p", CancellationToken.None));

        var failureLog = Assert.Single(capturingLogger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("cosmos.diagnostics", failureLog.Scope.Keys);
        Assert.Contains("cosmos.status_code", failureLog.Scope.Keys);
        Assert.Contains("cosmos.request_charge", failureLog.Scope.Keys);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, failureLog.Scope["cosmos.status_code"]);
        Assert.Equal(7.25, failureLog.Scope["cosmos.request_charge"]);
        Assert.Equal(ContainerId, failureLog.Scope["pinwiz.container"]);
        Assert.Equal("read", failureLog.Scope["pinwiz.operation"]);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_DoesNotLogDiagnostics()
    {
        // 404 is normal flow on cache misses; suppress the diagnostic
        // log scope so operators aren't paged by routine traffic.
        var (repo, container, capturingLogger) = NewRepository();
        container
            .ReadItemAsync<TestEntity>("missing", new PartitionKey("p"), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(MakeCosmosException(HttpStatusCode.NotFound, requestCharge: 1.0));

        await repo.GetByIdAsync("missing", "p", CancellationToken.None);

        Assert.DoesNotContain(capturingLogger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task UpsertAsync_Success_EmitsUpsertOperationTag()
    {
        var (repo, container, _) = NewRepository();
        var entity = new TestEntity { Id = "x", PartitionKey = "p", Name = "n" };
        var response = MakeItemResponse(entity, HttpStatusCode.OK, requestCharge: 5.0);
        container
            .UpsertItemAsync(entity, new PartitionKey("p"), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        using var listener = CollectCosmosMetrics(out var ru, out var duration);

        await repo.UpsertAsync(entity, CancellationToken.None);

        Assert.Contains(ru, s => s.Container == ContainerId && s.Operation == "upsert" && RuEquals(s.Value, 5.0));
        Assert.Contains(duration, s => s.Container == ContainerId && s.Operation == "upsert");
    }

    [Fact]
    public async Task DeleteAsync_Success_EmitsDeleteOperationTag()
    {
        var (repo, container, _) = NewRepository();
        var response = MakeItemResponse<TestEntity>(null, HttpStatusCode.NoContent, requestCharge: 5.5);
        container
            .DeleteItemAsync<TestEntity>("x", new PartitionKey("p"), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        using var listener = CollectCosmosMetrics(out var ru, out var duration);

        await repo.DeleteAsync("x", "p", CancellationToken.None);

        Assert.Contains(ru, s => s.Container == ContainerId && s.Operation == "delete" && RuEquals(s.Value, 5.5));
        Assert.Contains(duration, s => s.Container == ContainerId && s.Operation == "delete");
    }

    [Fact]
    public async Task StreamAsync_PerPage_EmitsOnePerPage()
    {
        // Per-page emission is the explicit choice — a multi-page query
        // might burn 30 RU per page; collapsing into one aggregate hides
        // that signal.
        var (repo, container, _) = NewRepository();
        var page1 = new[] { new TestEntity { Id = "1", PartitionKey = "p", Name = "a" } };
        var page2 = new[] { new TestEntity { Id = "2", PartitionKey = "p", Name = "b" } };
        container
            .GetItemQueryIterator<TestEntity>(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new MeteredFakeFeedIterator<TestEntity>(
            [
                (page1, 2.0),
                (page2, 3.5),
            ]));

        using var listener = CollectCosmosMetrics(out var ru, out var duration);

        await foreach (var _ in repo.StreamAsync("SELECT * FROM c", parameters: null, partitionKey: "p", CancellationToken.None))
        {
            // drain
        }

        var queryRu = ru.Where(s => s.Container == ContainerId && s.Operation == "query").ToList();
        Assert.Equal(2, queryRu.Count);
        Assert.Contains(queryRu, s => RuEquals(s.Value, 2.0));
        Assert.Contains(queryRu, s => RuEquals(s.Value, 3.5));
        Assert.Equal(2, duration.Count(s => s.Container == ContainerId && s.Operation == "query"));
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static (CosmosRepository<TestEntity> Repo, Container Container, CapturingLogger Logger) NewRepository()
    {
        var container = Substitute.For<Container>();
        container.Id.Returns(ContainerId);
        var logger = new CapturingLogger();
        var repo = new TestableRepository(container, logger);
        return (repo, container, logger);
    }

    private static ItemResponse<TItem> MakeItemResponse<TItem>(TItem? resource, HttpStatusCode statusCode, double requestCharge)
    {
        var response = Substitute.For<ItemResponse<TItem>>();
        response.Resource.Returns(resource!);
        response.StatusCode.Returns(statusCode);
        response.RequestCharge.Returns(requestCharge);
        return response;
    }

    private static CosmosException MakeCosmosException(HttpStatusCode statusCode, double requestCharge)
    {
        // CosmosException's RequestCharge is a property settable via ctor.
        return new CosmosException(
            message: "simulated",
            statusCode: statusCode,
            subStatusCode: 0,
            activityId: "test-activity",
            requestCharge: requestCharge);
    }

    private static MeterListener CollectCosmosMetrics(
        out ConcurrentBag<(double Value, string? Container, string? Operation)> ru,
        out ConcurrentBag<(double Value, string? Container, string? Operation)> duration)
    {
        var ruBag = new ConcurrentBag<(double, string?, string?)>();
        var durationBag = new ConcurrentBag<(double, string?, string?)>();
        ru = ruBag;
        duration = durationBag;

        var listener = new MeterListener();
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? containerTag = null;
            string? operationTag = null;
            foreach (var t in tags)
            {
                if (t.Key == "container") containerTag = t.Value as string;
                else if (t.Key == "operation") operationTag = t.Value as string;
            }
            if (instrument.Name == "pinwiz.cosmos.ru_charge")
            {
                ruBag.Add((value, containerTag, operationTag));
            }
            else if (instrument.Name == "pinwiz.cosmos.query_duration_ms")
            {
                durationBag.Add((value, containerTag, operationTag));
            }
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.CosmosRuCharge);
        listener.EnableMeasurementEvents(PinballWizardTelemetry.CosmosQueryDurationMs);
        return listener;
    }

    public sealed class TestEntity : IEntity
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("partitionKey")]
        public required string PartitionKey { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; set; }
    }

    // Concrete repo so tests can construct CosmosRepository<TestEntity>
    // directly (its ctor takes ILogger<CosmosRepository<TestEntity>>;
    // CapturingLogger is a non-generic ILogger so we pass it through
    // a small adapter).
    private sealed class TestableRepository : CosmosRepository<TestEntity>
    {
        public TestableRepository(Container container, ILogger inner)
            : base(container, new TypedAdapter(inner))
        {
        }

        private sealed class TypedAdapter : ILogger<CosmosRepository<TestEntity>>
        {
            private readonly ILogger _inner;
            public TypedAdapter(ILogger inner) { _inner = inner; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
            public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>
    /// In-memory <see cref="ILogger"/> that captures the last opened
    /// scope alongside each log entry. Necessary because the Diagnostics
    /// capture lives in <c>BeginScope</c> on the failure path; the
    /// production logger forwards to OTel + Console where structured
    /// scope state is preserved, but a plain test logger that only
    /// records <c>Log</c> arguments would miss the scope.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];
        private readonly Stack<IReadOnlyDictionary<string, object?>> _scopes = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                _scopes.Push(kvps.ToDictionary(p => p.Key, p => p.Value));
            }
            else
            {
                _scopes.Push(new Dictionary<string, object?>());
            }
            return new ScopePopper(_scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Snapshot the active scope (if any) at the moment of the
            // log call. The production code opens the diagnostic scope
            // immediately before LogError, so the active scope is the
            // diagnostic scope.
            var scope = _scopes.Count > 0
                ? _scopes.Peek()
                : (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>();
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), scope));
        }

        private sealed class ScopePopper : IDisposable
        {
            private readonly Stack<IReadOnlyDictionary<string, object?>> _scopes;
            private bool _disposed;
            public ScopePopper(Stack<IReadOnlyDictionary<string, object?>> scopes) { _scopes = scopes; }
            public void Dispose()
            {
                if (_disposed || _scopes.Count == 0) return;
                _scopes.Pop();
                _disposed = true;
            }
        }
    }

    public sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Scope);

    /// <summary>
    /// FeedIterator that supplies a per-page <see cref="FeedResponse{T}.RequestCharge"/>
    /// so streaming-query metric emission can be exercised end-to-end.
    /// </summary>
    private sealed class MeteredFakeFeedIterator<TItem> : FeedIterator<TItem>
    {
        private readonly Queue<(IReadOnlyList<TItem> items, double requestCharge)> _pages;

        public MeteredFakeFeedIterator(IEnumerable<(IReadOnlyList<TItem> items, double requestCharge)> pages)
        {
            _pages = new Queue<(IReadOnlyList<TItem>, double)>(pages);
        }

        public override bool HasMoreResults => _pages.Count > 0;

        public override Task<FeedResponse<TItem>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            var (items, ru) = _pages.Dequeue();
            return Task.FromResult<FeedResponse<TItem>>(new MeteredFakeFeedResponse<TItem>(items, ru));
        }
    }

    private sealed class MeteredFakeFeedResponse<TItem> : FeedResponse<TItem>
    {
        private readonly IReadOnlyList<TItem> _items;
        private readonly double _requestCharge;

        public MeteredFakeFeedResponse(IReadOnlyList<TItem> items, double requestCharge)
        {
            _items = items;
            _requestCharge = requestCharge;
        }

        public override int Count => _items.Count;
        public override string? ContinuationToken => null;
        public override Headers Headers => new();
        public override IEnumerable<TItem> Resource => _items;
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
        public override CosmosDiagnostics Diagnostics => null!;
        public override double RequestCharge => _requestCharge;
        public override string? ActivityId => null;
        public override string? ETag => null;
        public override string? IndexMetrics => null;
        public override IEnumerator<TItem> GetEnumerator() => _items.GetEnumerator();
    }
}


