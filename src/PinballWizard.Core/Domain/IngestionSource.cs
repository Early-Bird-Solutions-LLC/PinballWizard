using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// Per-manufacturer ingestion source configuration. Per ADR 0007 these
/// records are the source of truth for whether a source is enabled,
/// what cadence it runs at, and what politeness overrides apply —
/// editable at runtime via the Admin UI without a redeploy.
/// </summary>
/// <remarks>
/// One <see cref="IngestionSource"/> per manufacturer or third-party API
/// (Stern, JJP, American Pinball, OPDB, Pinball Map, etc.). The
/// <see cref="ScraperImplKey"/> maps to a registered concrete
/// <c>ISourceScraper</c> implementation in DI.
/// </remarks>
public sealed class IngestionSource : IEntity
{
    /// <summary>Source key — also the Cosmos document id (e.g., <c>stern</c>, <c>opdb</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Partition key. For ingestion sources we use a single logical
    /// partition (<c>config</c>) because the container is small and
    /// queries are always small-N enumerations.
    /// </summary>
    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; init; } = "config";

    /// <summary>Human-readable display name (e.g., "Stern Pinball").</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; set; }

    /// <summary>
    /// Maps to a registered concrete <c>ISourceScraper</c> implementation
    /// in the DI container (e.g., <c>stern</c>, <c>jjp</c>, <c>opdb</c>).
    /// Resolves at scraper-process startup; the value never changes for
    /// a given manufacturer (the implementation may change but the key
    /// stays stable).
    /// </summary>
    [JsonPropertyName("scraperImplKey")]
    public required string ScraperImplKey { get; set; }

    /// <summary>Source-site root URL.</summary>
    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; set; }

    /// <summary>If false, scheduled runs are no-ops. The ACA Job still spins up but exits immediately.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Cadence: <c>daily</c> / <c>weekly</c> / <c>monthly</c> / <c>manual</c>.</summary>
    [JsonPropertyName("cadence")]
    public required string Cadence { get; set; }

    /// <summary>Per-source overrides for politeness invariants. Optional; null means use defaults.</summary>
    [JsonPropertyName("politenessOverrides")]
    public PolitenessOverrides? PolitenessOverrides { get; set; }

    /// <summary>Last time this source's scraper started.</summary>
    [JsonPropertyName("lastRunAt")]
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Last time this source's scraper finished successfully (no errors logged).</summary>
    [JsonPropertyName("lastSuccessAt")]
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Counter — total documents discovered across all runs.</summary>
    [JsonPropertyName("totalDocumentsDiscovered")]
    public long TotalDocumentsDiscovered { get; set; }

    /// <summary>Counter — total run failures across all runs (any non-zero exit).</summary>
    [JsonPropertyName("totalRunFailures")]
    public long TotalRunFailures { get; set; }

    /// <summary>Cosmos system-managed _etag.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// Politeness invariants overridable per source. When a field is null on
/// the override, the global default (in <c>PoliteScraperOptions</c>, see
/// Gate 2 PR) applies.
/// </summary>
public sealed class PolitenessOverrides
{
    /// <summary>Per-request floor delay in milliseconds. Never zero.</summary>
    [JsonPropertyName("requestDelayMs")]
    public int? RequestDelayMs { get; set; }

    /// <summary>Override path for robots.txt. Null = use <c>/robots.txt</c>.</summary>
    [JsonPropertyName("robotsTxtPath")]
    public string? RobotsTxtPath { get; set; }

    /// <summary>Suffix appended to the User-Agent string for this source.</summary>
    [JsonPropertyName("userAgentSuffix")]
    public string? UserAgentSuffix { get; set; }

    /// <summary>Maximum consecutive 429s before the source aborts the run.</summary>
    [JsonPropertyName("max429Streak")]
    public int? Max429Streak { get; set; }
}
