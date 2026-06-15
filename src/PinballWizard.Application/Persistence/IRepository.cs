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
    /// determine where it lands. Returns the SAME entity instance that
    /// was passed in.
    /// </summary>
    /// <remarks>
    /// Per <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 2</see>
    /// the Cosmos client is configured with <c>EnableContentResponseOnWrite = false</c>,
    /// so the underlying store does NOT round-trip the persisted resource
    /// (saving one round-trip + ~1 RU per write). The returned entity is
    /// therefore the input entity, unchanged. Callers that need the
    /// server-populated <c>ETag</c> for optimistic-concurrency conditional
    /// writes will need to opt back into <c>EnableContentResponseOnWrite</c>
    /// at the per-request level — a separate decision per ADR-0025 § 7
    /// (deferred until a 2nd writer of <see cref="IMachineRepository"/>
    /// lands).
    /// </remarks>
    Task<T> UpsertAsync(T entity, CancellationToken cancellationToken);

    /// <summary>
    /// Delete a document by id within the given partition. No-op if the
    /// document does not exist (deletion is idempotent).
    /// </summary>
    Task DeleteAsync(string id, string partitionKey, CancellationToken cancellationToken);

    /// <summary>
    /// Stream documents matching a SQL query within a SINGLE partition
    /// (Tier 1 per ADR-0036). Pages are pulled lazily.
    /// </summary>
    /// <param name="partitionKey">Partition to scope the query to. Required — a single-partition read.</param>
    IAsyncEnumerable<T> StreamAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        string partitionKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stream documents matching a SQL query ACROSS ALL PARTITIONS
    /// (fan-out). Per ADR-0036 this is a Tier 2 escape hatch: permitted
    /// ONLY for back-office/admin/startup paths over a provably bounded
    /// set, and every call site MUST be listed in
    /// CrossPartitionQueryAllowListTests. User-facing or unbounded
    /// aggregate reads MUST use a Tier 3 projection instead.
    /// </summary>
    IAsyncEnumerable<T> StreamCrossPartitionAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken);
}
