using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the OPDB (Open Pinball Database) integration —
/// the canonical machine catalog the project uses to identify pinball
/// machines across all manufacturer sources.
/// </summary>
/// <remarks>
/// Phase 1.1 of the parallel execution plan. OPDB is API-based (not a
/// site scraper), so it is the lowest-risk first integration on the
/// Clean Architecture layout — validates the layout cleanly
/// accommodates non-Stern sources before per-manufacturer scrapers
/// start landing.
/// <para>
/// API token: get one at https://opdb.org/api by registering. The
/// token is a simple bearer credential and MUST NOT be committed.
/// Set it via environment variable <c>OPDB__APITOKEN</c> (the double
/// underscore is the .NET configuration convention for nested keys),
/// the user-secrets store in development, or an Azure Key Vault
/// secret in production (referenced through Key Vault Configuration
/// Provider when Phase 4 lands).
/// </para>
/// </remarks>
public sealed class OpdbOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Opdb";

    /// <summary>
    /// Full configuration key for <see cref="BaseUrl"/>. Exposed so
    /// callers (e.g., the CLI's gating logic that decides whether to
    /// register the OPDB integration) can presence-check the key
    /// without duplicating the <c>"Opdb:BaseUrl"</c> string and risking
    /// a silent drift if the section is ever renamed.
    /// </summary>
    public const string BaseUrlKey = $"{SectionName}:{nameof(BaseUrl)}";

    /// <summary>OPDB API base URL. Defaults to the public production endpoint.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://opdb.org/api/";

    /// <summary>
    /// Bearer token for OPDB API requests. Required for any non-public
    /// endpoint. Empty string is allowed during local-only sync runs
    /// against the freely-available <c>/api/changelog</c> endpoint, but
    /// is rejected for any sync that touches the machines list.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Total HTTP timeout in seconds. Governs both the
    /// <see cref="HttpClient.Timeout"/> on the typed OPDB client and the
    /// <c>TotalRequestTimeout</c> on the OPDB-specific resilience handler.
    /// OPDB's <c>/api/export</c> returns the entire machine catalog in a
    /// single response (~2.4&#160;MB / ~2,360 records as of 2026-05-04) and
    /// can take 30s+ when OPDB's CDN cache is cold or rate-limiting is in
    /// effect; defaulting to 120s gives reasonable headroom while still
    /// bounding hung calls.
    /// </summary>
    [Range(5, 600)]
    public int HttpTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// On-disk cache path for the <c>/api/export</c> response. Set to a
    /// relative path under the project root (default
    /// <c>data/cache/opdb-export.json</c>) or an absolute path for
    /// production deployments. Empty / whitespace disables the cache —
    /// every call hits the network. The cache stores the raw OPDB
    /// response body verbatim; freshness is judged by file modification
    /// time vs <see cref="ExportCacheTtlSeconds"/>.
    /// </summary>
    /// <remarks>
    /// OPDB's published policy on <c>/api/export</c> is "once per hour"
    /// (https://opdb.org/api). Re-running the sync within the hour
    /// without a cache produces hard 429s and can cascade into a
    /// multi-hour cooldown. The cache eliminates the rate-limit problem
    /// for any repeat invocation within the TTL window — debug runs,
    /// dry-runs, and applies all share the same fetch.
    /// </remarks>
    public string ExportCachePath { get; set; } = "data/cache/opdb-export.json";

    /// <summary>
    /// Time-to-live for the on-disk export cache, in seconds. Default
    /// 3600 (1 hour) matches OPDB's published once-per-hour policy on
    /// <c>/api/export</c>; setting longer is fine (the catalog rarely
    /// changes within a day) but shorter risks tripping the rate limit.
    /// Set to 0 to force every call to bypass the cache (the file is
    /// still written on success, so a subsequent run with a non-zero
    /// TTL can use it).
    /// </summary>
    [Range(0, 86400)]
    public int ExportCacheTtlSeconds { get; set; } = 3600;
}
