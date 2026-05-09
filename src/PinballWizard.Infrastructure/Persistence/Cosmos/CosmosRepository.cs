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
            var response = await _container.ReadItemAsync<T>(
                id,
                new PartitionKey(partitionKey),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Resource;
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
        await _container.UpsertItemAsync(
            entity,
            new PartitionKey(entity.PartitionKey),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return entity;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, string partitionKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        try
        {
            await _container.DeleteItemAsync<T>(
                id,
                new PartitionKey(partitionKey),
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in page)
            {
                yield return item;
            }
        }
    }
}
