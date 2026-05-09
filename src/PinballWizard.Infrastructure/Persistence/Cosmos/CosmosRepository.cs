using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Generic Cosmos-backed implementation of <see cref="IRepository{T}"/>.
/// Concrete per-entity repositories extend this class to add
/// entity-specific query methods.
/// </summary>
/// <remarks>
/// Wraps the Cosmos SDK <see cref="Container"/> and translates SDK
/// behaviors into the repository contract:
/// <list type="bullet">
///   <item>404 on read returns null.</item>
///   <item>404 on delete is swallowed (idempotent deletion).</item>
///   <item>Streaming reads paginate via <see cref="FeedIterator{T}"/>.</item>
///   <item>Every SDK call routes through <see cref="ExecuteWithMetricsAsync{TResult}"/>
///     so RU + duration land on <c>pinwiz.cosmos.ru_charge</c> /
///     <c>pinwiz.cosmos.query_duration_ms</c> per <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 8</see>,
///     and <see cref="CosmosException.Diagnostics"/> is captured into a
///     structured log scope on non-404 failures.</item>
/// </list>
/// Retries, rate-limit handling, and connection pooling are delegated
/// to the SDK's built-in policies.
/// </remarks>
public class CosmosRepository<T> : IRepository<T> where T : class, IEntity
{
    private readonly Container _container;
    private readonly ILogger _logger;

    /// <summary>Direct access to the wrapped container — for derived classes that need to issue specialized queries.</summary>
    protected Container Container => _container;

    /// <summary>Logger for use by derived classes.</summary>
    protected ILogger Logger => _logger;

    /// <summary>
    /// Initializes a new repository wrapping <paramref name="container"/>.
    /// </summary>
    public CosmosRepository(Container container, ILogger<CosmosRepository<T>> logger)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(logger);
        _container = container;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<T?> GetByIdAsync(string id, string partitionKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        try
        {
            return await ExecuteWithMetricsAsync(
                "read",
                async ct =>
                {
                    var response = await _container.ReadItemAsync<T>(
                        id,
                        new PartitionKey(partitionKey),
                        cancellationToken: ct).ConfigureAwait(false);
                    return (response.Resource, response.RequestCharge);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<T> UpsertAsync(T entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Per ADR-0025 § 2 the CosmosClient is configured with
        // `EnableContentResponseOnWrite = false`, so `response.Resource`
        // is null. Return the input `entity` directly — caller already
        // holds the canonical instance. Saves one round-trip + ~1 RU per
        // write. Optimistic-concurrency conditional writes (which would
        // need the server-populated ETag) are deferred per ADR-0025 § 7.
        await ExecuteWithMetricsAsync(
            "upsert",
            async ct =>
            {
                var response = await _container.UpsertItemAsync(
                    entity,
                    new PartitionKey(entity.PartitionKey),
                    cancellationToken: ct).ConfigureAwait(false);
                return (entity, response.RequestCharge);
            },
            cancellationToken).ConfigureAwait(false);
        return entity;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, string partitionKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        try
        {
            await ExecuteWithMetricsAsync(
                "delete",
                async ct =>
                {
                    var response = await _container.DeleteItemAsync<T>(
                        id,
                        new PartitionKey(partitionKey),
                        cancellationToken: ct).ConfigureAwait(false);
                    return (true, response.RequestCharge);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent deletion — already gone counts as success.
            _logger.LogDebug("Delete of {EntityType} {Id} in partition {PartitionKey} found nothing — treating as success.", typeof(T).Name, id, partitionKey);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> StreamAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        string? partitionKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var queryDefinition = new QueryDefinition(query);
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                queryDefinition = queryDefinition.WithParameter('@' + name, value);
            }
        }

        var requestOptions = new QueryRequestOptions();
        if (partitionKey is not null)
        {
            requestOptions.PartitionKey = new PartitionKey(partitionKey);
        }

        using var iterator = _container.GetItemQueryIterator<T>(queryDefinition, requestOptions: requestOptions);
        while (iterator.HasMoreResults)
        {
            // One observation per page so heavy multi-page queries
            // don't get hidden inside a single aggregate. Per-page RU
            // is also more useful for spotting a single expensive page
            // (e.g., a hot partition's first page) than a sum is.
            var page = await ExecuteWithMetricsAsync(
                "query",
                async ct =>
                {
                    var p = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                    return (p, p.RequestCharge);
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var item in page)
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Boundary-instrumentation helper per <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 8</see>.
    /// Wraps a Cosmos SDK call in a <see cref="Stopwatch"/> + try/catch,
    /// emits the operation's <see cref="ResponseMessage.RequestCharge"/>
    /// (or <see cref="CosmosException.RequestCharge"/> on failure) plus
    /// wall-clock duration to <c>pinwiz.cosmos.ru_charge</c> /
    /// <c>pinwiz.cosmos.query_duration_ms</c>, and on a non-404
    /// <see cref="CosmosException"/> captures
    /// <see cref="CosmosException.Diagnostics"/> into a structured log
    /// scope before rethrowing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exposed <c>protected</c> so concrete repositories with specialized
    /// query methods (e.g. <c>MachineRepository.QueryByTitleAsync</c>'s
    /// cross-partition `STRINGEQUALS`) can wrap their own SDK calls
    /// without re-implementing the boundary capture. The original
    /// ADR-0025 § 8 design framed this emission as a
    /// <c>MeteredCosmosRepository&lt;T&gt;</c> decorator over
    /// <see cref="IRepository{T}"/>; that approach was rejected during
    /// PR 4 because <see cref="IRepository{T}"/> is Cosmos-agnostic and
    /// does not surface <see cref="ResponseMessage.RequestCharge"/> —
    /// a decorator could capture duration only. Inline emission at the
    /// SDK boundary captures both, and keeps the metric-emission
    /// pattern in one place rather than spread across a base + a
    /// decorator that would have to be kept in sync.
    /// </para>
    /// <para>
    /// 404 status codes are swallowed by the calling public methods
    /// (<see cref="GetByIdAsync"/> returns <c>null</c>;
    /// <see cref="DeleteAsync"/> treats as idempotent success) — they
    /// are normal flow, not failures. The metrics still record on the
    /// 404 path so RU spent looking for a missing item is visible;
    /// only the diagnostic log scope is suppressed for 404s so
    /// operators aren't paged on routine cache misses.
    /// </para>
    /// </remarks>
    protected async Task<TResult> ExecuteWithMetricsAsync<TResult>(
        string operation,
        Func<CancellationToken, Task<(TResult result, double requestCharge)>> action,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (result, ru) = await action(cancellationToken).ConfigureAwait(false);
            EmitCosmosMetrics(operation, stopwatch.Elapsed, ru);
            return result;
        }
        catch (CosmosException ex)
        {
            EmitCosmosMetrics(operation, stopwatch.Elapsed, ex.RequestCharge);
            if (ex.StatusCode != HttpStatusCode.NotFound)
            {
                LogCosmosFailureWithDiagnostics(ex, operation);
            }
            throw;
        }
        // OperationCanceledException is intentionally NOT caught — it
        // propagates to honour graceful shutdown (host stop, request
        // cancellation). The trade-off: the in-flight duration metric is
        // not emitted on the cancellation path. Acceptable because
        // cancellations aren't failures and shouldn't pollute the latency
        // distribution; alerting on cancellation cadence happens elsewhere
        // (ACA worker lifecycle telemetry).
    }

    private void EmitCosmosMetrics(string operation, TimeSpan duration, double requestCharge)
    {
        // Two `KeyValuePair<string, object?>` allocations per SDK call.
        // Below the noise floor at curated-subset scale (hundreds of
        // documents). If a future hot-path emerges (Phase 4.5 corpus
        // expansion, bulk-execution paths driving thousands of ops/sec),
        // promote to a per-(container, operation) `TagList` cache to
        // amortize the allocation — same pattern OTel SDK uses
        // internally for high-frequency callsites.
        var containerTag = new KeyValuePair<string, object?>("container", _container.Id);
        var operationTag = new KeyValuePair<string, object?>("operation", operation);
        PinballWizardTelemetry.CosmosRuCharge.Record(requestCharge, containerTag, operationTag);
        PinballWizardTelemetry.CosmosQueryDurationMs.Record(duration.TotalMilliseconds, containerTag, operationTag);
    }

    private void LogCosmosFailureWithDiagnostics(CosmosException ex, string operation)
    {
        // CosmosException.Diagnostics carries region, retry count, RU
        // consumed by the failed call, and per-stage timing — this is
        // the load-bearing operator surface when a 429 / 503 / 408
        // surfaces. Capture it into a structured log scope so the next
        // App Insights query lands the operator on the failure context
        // without needing a separate trace lookup.
        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["pinwiz.container"] = _container.Id,
            ["pinwiz.operation"] = operation,
            ["cosmos.status_code"] = (int)ex.StatusCode,
            ["cosmos.sub_status_code"] = ex.SubStatusCode,
            ["cosmos.activity_id"] = ex.ActivityId,
            ["cosmos.request_charge"] = ex.RequestCharge,
            ["cosmos.diagnostics"] = ex.Diagnostics?.ToString(),
        }))
        {
            _logger.LogError(
                ex,
                "Cosmos {Operation} on container {Container} failed: {StatusCode}. Diagnostics captured in log scope.",
                operation, _container.Id, ex.StatusCode);
        }
    }
}
