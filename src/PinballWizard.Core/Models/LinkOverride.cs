namespace PinballWizard.Core.Models;

public sealed class LinkOverrideRecord
{
    // Unique pattern identifier: normalized "discovery_url|document_type".
    // URL is lowercased and trailing slashes normalized. This is both the partition key
    // and the document id for deterministic upserts.
    public required string SourcePattern { get; init; }

    // Machine IDs to which this pattern's documents should link.
    // Empty array means this pattern is confirmed platform-generic (no machine scope).
    public required string[] MachineIds { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? Notes { get; init; }

    public static string BuildSourcePattern(string discoveryUrl, DocumentType documentType)
        => $"{discoveryUrl.TrimEnd('/')}|{documentType}";
}
