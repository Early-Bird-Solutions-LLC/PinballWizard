using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace PinballWizard.Core.Models;

/// <summary>
/// The core metadata unit. Every downloaded file gets a DocumentRecord that travels
/// with it through the entire pipeline: scraping → downloading → RAG indexing → query response.
/// </summary>
public sealed class DocumentRecord
{
    /// <summary>
    /// Deterministic ID derived from the canonical file URL (SHA-256 prefix).
    /// Same PDF found on /manuals/ and /game/stranger-things/ maps to one document.
    /// </summary>
    public required string DocumentId { get; init; }

    public required SourceInfo Source { get; set; }
    public required ClassificationInfo Classification { get; set; }
    public GameReference? Game { get; set; }
    public DownloadedFileInfo? File { get; set; }
    public HttpMetadata? Http { get; set; }
    public required TimelineInfo Timeline { get; set; }
    public List<CrossReference> CrossReferences { get; set; } = [];
    public string? RunId { get; set; }

    // Canonical manufacturer name, denormalized from the scraper that produced this record.
    // Stored in Cosmos for filtering; set by ScraperOrchestrator at upsert time.
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Generates a deterministic document ID from a canonical file URL.
    /// </summary>
    public static string GenerateId(string fileUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fileUrl.ToLowerInvariant()));
        return $"doc_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
}

/// <summary>
/// Where and when we found this file. Immutable per discovery.
/// </summary>
public sealed class SourceInfo
{
    /// <summary>The page we were on when we found this file.</summary>
    public required string DiscoveryUrl { get; init; }

    /// <summary>Human-readable context: "Manuals Page", "Game Page → Specs &amp; Manual tab".</summary>
    public required string DiscoveryContext { get; init; }

    /// <summary>Direct link to the file on sternpinball.com.</summary>
    public required string FileUrl { get; init; }

    /// <summary>The anchor/button text that linked to this file.</summary>
    public string? LinkText { get; init; }

    /// <summary>How the link appeared on the page.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionType ActionType { get; init; }

    /// <summary>Which source type discovered this.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SourceType SourceType { get; init; }

    /// <summary>Which tab on a game page this was found under (game pages only).</summary>
    public string? Tab { get; init; }

    /// <summary>When this source entry was created.</summary>
    public DateTime ScrapedAt { get; init; }
}

/// <summary>
/// What kind of document this is and what it contains.
/// </summary>
public sealed class ClassificationInfo
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DocumentType DocumentType { get; set; }

    public List<ContentCategory> ContentCategories { get; set; } = [];

    /// <summary>pdf, zip, spk, jpg, etc.</summary>
    public required string FileFormat { get; set; }
}

/// <summary>
/// Links this document to a specific game and edition.
/// </summary>
public sealed class GameReference
{
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public string? Edition { get; init; }
    public required string GamePageUrl { get; init; }
}

/// <summary>
/// Information about the downloaded file on local disk.
/// </summary>
public sealed class DownloadedFileInfo
{
    /// <summary>Relative path within the data directory.</summary>
    public required string LocalPath { get; set; }

    public required string Filename { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? MimeType { get; set; }
    public int? PageCount { get; set; }
}

/// <summary>
/// Permanent download-skip marker stamped when a download cannot succeed under the
/// current configuration — e.g. the file exceeds the configured size cap.
/// Stored in the raw record so future runs skip without re-attempting.
/// </summary>
public sealed class DownloadSkipInfo
{
    /// <summary>
    /// Why the download was permanently skipped. Use the <c>Reasons.*</c> constants.
    /// Stored as a snake_case string on the wire so Cosmos is queryable without
    /// knowing the C# type (mirrors <c>link_status</c>).
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// File size in bytes as reported by the <c>Content-Length</c> response header
    /// at skip time. Null when the server omitted the header.
    /// When non-null, a future run re-attempts only if the current cap is >=
    /// <see cref="ObservedSizeBytes"/> (the file now fits). When null, a future run
    /// re-attempts when the cap has been raised above <see cref="CapBytesAtSkip"/>.
    /// </summary>
    public long? ObservedSizeBytes { get; init; }

    /// <summary>
    /// <c>MaxFileSizeBytes</c> that was active when this skip was recorded.
    /// Used for cap-raise detection: if the current cap exceeds this value (and
    /// <see cref="ObservedSizeBytes"/> is unknown), the document is re-attempted.
    /// </summary>
    public required long CapBytesAtSkip { get; init; }

    /// <summary>UTC timestamp when this skip marker was stamped.</summary>
    public required DateTime SkippedAt { get; init; }

    /// <summary>Well-known <see cref="Reason"/> constants.</summary>
    public static class Reasons
    {
        /// <summary>
        /// The file's <c>Content-Length</c> (or streamed size) exceeded the configured
        /// <c>MaxFileSizeBytes</c>. This is a permanent property under the current cap —
        /// not a transient error — so the record is skipped until the cap is raised.
        /// </summary>
        public const string TooLarge = "too_large";

        /// <summary>
        /// The server returned a permanent client-side rejection: HTTP 403 Forbidden,
        /// 404 Not Found, or 410 Gone. These are structurally undownloadable under the
        /// current URL — the resource is gone or access-controlled at the origin level.
        /// The record is skipped on every future run; <c>--force-redownload</c> bypasses
        /// the skip for an operator-driven re-check.
        /// </summary>
        public const string PermanentRejection = "permanent_rejection";
    }
}

/// <summary>
/// HTTP metadata from the server response, used for conditional requests.
/// </summary>
public sealed class HttpMetadata
{
    public DateTime? LastModified { get; set; }
    public string? ETag { get; set; }
    public string? ContentType { get; set; }
    public long? ContentLength { get; set; }
}

/// <summary>
/// Tracks the lifecycle of this document across scraper runs.
/// </summary>
public sealed class TimelineInfo
{
    public required DateTime FirstDiscoveredAt { get; init; }
    public DateTime? FirstDownloadedAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastDownloadedAt { get; set; }
    public DateTime? LastContentChangedAt { get; set; }
    public int VersionCount { get; set; } = 1;
}

/// <summary>
/// When the same file URL is found on multiple pages, each additional
/// discovery is recorded as a cross-reference.
/// </summary>
public sealed class CrossReference
{
    public required string AlsoFoundAt { get; init; }
    public required string DiscoveryContext { get; init; }
    public string? LinkText { get; init; }
    public DateTime DiscoveredAt { get; init; }
}
