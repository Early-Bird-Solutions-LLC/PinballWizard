using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Cosmos wire-format POCO for the `scraped_documents_raw` container.
//
// Class name is RawDocumentCosmosRecord (not RawDocumentRecord) to avoid
// namespace collision with PinballWizard.Core.Models.RawDocumentRecord.
//
// Partition key path: /document_id. id == document_id.
// All JSON field names are snake_case via [JsonPropertyName(...)].
//
// The link_status field is stored as a string on the wire so that the
// container is queryable via SQL literals without Cosmos needing to know
// about the C# enum definition. Valid values: "pending", "linked",
// "platform_generic", "not_in_catalog", "failed", "manually_linked".
internal sealed class RawDocumentCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    // IEntity.PartitionKey — partition key path is /document_id.
    [JsonPropertyName("document_id")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("document_url")]
    public required string DocumentUrl { get; init; }

    [JsonPropertyName("document_type")]
    public required string DocumentType { get; init; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("link_status")]
    public required string LinkStatus { get; set; }

    [JsonPropertyName("resolution_strategy")]
    public string? ResolutionStrategy { get; set; }

    [JsonPropertyName("link_attempted_at")]
    public DateTimeOffset? LinkAttemptedAt { get; set; }

    [JsonPropertyName("link_failure_reason")]
    public string? LinkFailureReason { get; set; }

    [JsonPropertyName("linked_by")]
    public string? LinkedBy { get; set; }

    [JsonPropertyName("linked_at")]
    public DateTimeOffset? LinkedAt { get; set; }

    [JsonPropertyName("override_id")]
    public string? OverrideId { get; set; }

    [JsonPropertyName("linked_machine_ids")]
    public List<string> LinkedMachineIds { get; set; } = [];

    [JsonPropertyName("source")]
    public RawSourceInfo? Source { get; set; }

    [JsonPropertyName("classification")]
    public RawClassificationInfo? Classification { get; set; }

    [JsonPropertyName("file")]
    public RawFileInfo? File { get; set; }

    [JsonPropertyName("http")]
    public RawHttpInfo? Http { get; set; }

    [JsonPropertyName("timeline")]
    public RawTimelineInfo? Timeline { get; set; }

    [JsonPropertyName("cross_references")]
    public List<RawCrossRef> CrossReferences { get; set; } = [];
}

internal sealed class RawSourceInfo
{
    [JsonPropertyName("discovery_url")]
    public required string DiscoveryUrl { get; init; }

    [JsonPropertyName("discovery_context")]
    public required string DiscoveryContext { get; init; }

    [JsonPropertyName("file_url")]
    public required string FileUrl { get; init; }

    [JsonPropertyName("link_text")]
    public string? LinkText { get; init; }

    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }

    [JsonPropertyName("action_type")]
    public string? ActionType { get; init; }

    [JsonPropertyName("tab")]
    public string? Tab { get; init; }

    [JsonPropertyName("scraped_at")]
    public DateTime ScrapedAt { get; init; }
}

internal sealed class RawClassificationInfo
{
    [JsonPropertyName("document_type")]
    public required string DocumentType { get; init; }

    [JsonPropertyName("file_format")]
    public required string FileFormat { get; init; }
}

internal sealed class RawFileInfo
{
    [JsonPropertyName("local_path")]
    public required string LocalPath { get; init; }

    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    [JsonPropertyName("page_count")]
    public int? PageCount { get; init; }
}

internal sealed class RawHttpInfo
{
    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    [JsonPropertyName("last_modified")]
    public DateTime? LastModified { get; init; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    [JsonPropertyName("content_length")]
    public long? ContentLength { get; init; }
}

internal sealed class RawTimelineInfo
{
    [JsonPropertyName("first_discovered_at")]
    public DateTime FirstDiscoveredAt { get; init; }

    [JsonPropertyName("last_checked_at")]
    public DateTime? LastCheckedAt { get; set; }

    [JsonPropertyName("last_downloaded_at")]
    public DateTime? LastDownloadedAt { get; init; }

    [JsonPropertyName("last_content_changed_at")]
    public DateTime? LastContentChangedAt { get; init; }

    [JsonPropertyName("version_count")]
    public int VersionCount { get; init; }
}

internal sealed class RawCrossRef
{
    [JsonPropertyName("also_found_at")]
    public required string AlsoFoundAt { get; init; }

    [JsonPropertyName("discovery_context")]
    public required string DiscoveryContext { get; init; }

    [JsonPropertyName("link_text")]
    public string? LinkText { get; init; }

    [JsonPropertyName("discovered_at")]
    public DateTime DiscoveredAt { get; init; }
}
