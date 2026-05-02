using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Persistence;

/// <summary>
/// Generic repository surface for any <see cref="IEntity"/>. Concrete
/// per-entity repositories (<see cref="IMachineRepository"/>,
/// <see cref="IIngestionSourceRepository"/>, etc.) extend this with
/// entity-specific query methods.
/// </summary>
/// <remarks>
/// All operations take a <see cref="CancellationToken"/> as the last
/// parameter per the engineering standards (§1.4). Streaming reads
/// return <see cref="IAsyncEnumerable{T}"/> so the caller pulls one
/// page at a time without materializing the whole result set.
/// </remarks>
public interface IRepository<T> where T : class, IEntity
{
    /// <summary>
    /// Read a single document by id within the given partition. Returns
    /// <c>null</c> if no document exists with that id in that partition.
    /// </summary>
    Task<T?> GetByIdAsync(string id, string partitionKey, CancellationToken cancellationToken);

    /// <summary>
    /// Upsert (insert-or-replace) a document. The document's
    /// <see cref="IEntity.Id"/> and <see cref="IEntity.PartitionKey"/>
    /// determine where it lands. Returns the persisted entity (with
    /// <c>ETag</c> populated when the underlying store provides one).
    /// </summary>
    Task<T> UpsertAsync(T entity, CancellationToken cancellationToken);

    /// <summary>
    /// Delete a document by id within the given partition. No-op if the
    /// document does not exist (deletion is idempotent).
    /// </summary>
    Task DeleteAsync(string id, string partitionKey, CancellationToken cancellationToken);

    /// <summary>
    /// Stream documents matching a SQL query, optionally scoped to a
    /// partition. Pages are pulled lazily — the caller controls
    /// throughput by enumerating slowly or quickly.
    /// </summary>
    /// <param name="query">SQL text. Parameters supplied via <paramref name="parameters"/>.</param>
    /// <param name="parameters">Named parameter bag (parameter names without the <c>@</c> prefix).</param>
    /// <param name="partitionKey">Partition to scope the query to. <c>null</c> = cross-partition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<T> StreamAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        string? partitionKey,
        CancellationToken cancellationToken);
}
