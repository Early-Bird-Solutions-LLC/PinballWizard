namespace PinballWizard.Core.Domain;

/// <summary>
/// Marker interface for any aggregate that's persisted as a Cosmos document.
/// Carries the two pieces of metadata Cosmos needs to address the document:
/// the document <see cref="Id"/> (unique within a partition) and the
/// <see cref="PartitionKey"/> value (the path is declared on the container).
/// </summary>
/// <remarks>
/// Implementing types are responsible for setting <see cref="Id"/> in a way
/// that respects the cross-partition uniqueness expectation for their
/// container. Most entities pick a deterministic ID derived from a
/// natural key (for example, OPDB ID for <see cref="Machine"/>) so that
/// re-discovery does not produce duplicates — see ADR 0002 for the
/// canonical ID-derivation pattern.
/// </remarks>
public interface IEntity
{
    /// <summary>
    /// Cosmos document id. Must be unique within the partition. Serialized
    /// as the lower-case <c>id</c> field per Cosmos requirements.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Value of the partition key for this document. The key path is
    /// declared on the container; this property is what gets written to
    /// that path. For per-user containers this is the Entra OID. For the
    /// machines container this is the manufacturer key (e.g., "stern").
    /// </summary>
    string PartitionKey { get; }
}
