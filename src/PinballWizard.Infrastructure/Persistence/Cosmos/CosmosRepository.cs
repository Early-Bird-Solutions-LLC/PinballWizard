using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
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
                cancellationToken,
                documentId: id).ConfigureAwait(false);
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
    public IAsyncEnumerable<T> StreamAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        return StreamCoreAsync(query, parameters, partitionKey, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<T> StreamCrossPartitionAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return StreamCoreAsync(query, parameters, partitionKey: null, cancellationToken);
    }

    private async IAsyncEnumerable<T> StreamCoreAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        string? partitionKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

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
        CancellationToken cancellationToken,
        string? documentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);

        return await CosmosMetricsHelper.ExecuteWithMetricsAsync(
            _container.Id, operation, _logger, action, cancellationToken, documentId).ConfigureAwait(false);
    }
}
