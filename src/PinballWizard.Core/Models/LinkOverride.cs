namespace PinballWizard.Core.Models;

/// <summary>
/// Domain model for a <c>link_overrides</c> Cosmos record.
/// Partition key: <c>source_pattern</c>. One record per pattern (upsert semantics).
///
/// Represents an administrative override that maps a document-discovery pattern
/// (identified by normalized discovery_url and document_type) to a set of machine IDs
/// or marks it as platform-generic. Used during catalog linking to resolve ambiguous
/// or platform-specific documents.
///
/// The <c>id</c> field equals <c>source_pattern</c> — this ensures one override per pattern
/// with deterministic lookups.
/// </summary>
public sealed class LinkOverrideRecord
{
    /// <summary>
    /// Unique pattern identifier: normalized "discovery_url|document_type".
    /// URL is lowercased and trailing slashes normalized. This is both the partition key
    /// and the document id for deterministic upserts.
    ///
    /// Example: "https://example.com/support|Manual"
    /// </summary>
    public required string SourcePattern { get; init; }

    /// <summary>
    /// Machine IDs to which this pattern's documents should link.
    /// Empty array means this pattern is confirmed platform-generic (no machine scope).
    /// </summary>
    public required string[] MachineIds { get; init; }

    /// <summary>
    /// User or system that created this override (e.g., "admin@earlybird", "import/v1").
    /// </summary>
    public required string CreatedBy { get; init; }

    /// <summary>
    /// When this override was created or last updated.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Optional human-readable explanation for why this override exists.
    /// </summary>
    public string? Notes { get; init; }

    // Builds the deterministic source pattern key from a discovery URL and document type.
    // The pattern is the canonical partition key / id for this container.
    public static string BuildSourcePattern(string discoveryUrl, DocumentType documentType)
        => $"{discoveryUrl.TrimEnd('/')}|{documentType}";
}
