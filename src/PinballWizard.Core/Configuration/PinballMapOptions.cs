using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Pinball Map (pinballmap.com) integration. The
/// public Pinball Map API exposes location-by-region data (each location
/// carries the machines on-site, each machine carries an OPDB id linking
/// it to our canonical machine catalog) and is the project's first
/// non-OPDB external-API source — Phase 3 Wave 1.
/// </summary>
/// <remarks>
/// Authentication is anonymous: the public read-only API endpoints under
/// <c>/api/v1/region/{region}/locations.json</c> require no token. We
/// follow the same on-disk cache + per-source politeness pattern as
/// <see cref="OpdbOptions"/> so repeat syncs within the TTL window do
/// not hit the network. Politeness defaults are conservative (5&#160;s
/// per-request delay vs the site's published <c>Crawl-delay: 3</c>) and
/// per-source overrides land in the seed manifest at
/// <c>data/seeds/ingestion_sources.v1.json</c>.
/// <para>
/// Phase 3 scope: the client fetches a single region by name on demand
/// (used as the "citation showcase" surface for the Wizard answer flow).
/// Enumerating every region is deferred until a downstream consumer
/// needs it; the client design accommodates that future need without a
/// breaking change.
/// </para>
/// </remarks>
public sealed class PinballMapOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "PinballMap";

    /// <summary>
    /// Full configuration key for <see cref="BaseUrl"/>. Exposed so
    /// callers (e.g., the CLI's gating logic that decides whether to
    /// register the integration) can presence-check the key without
    /// duplicating the <c>"PinballMap:BaseUrl"</c> string.
    /// </summary>
    public const string BaseUrlKey = $"{SectionName}:{nameof(BaseUrl)}";

    /// <summary>Pinball Map API base URL. Defaults to the public production endpoint.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://pinballmap.com/api/v1/";

    /// <summary>
    /// Total HTTP timeout in seconds. Governs the
    /// <see cref="HttpClient.Timeout"/> on the typed Pinball Map client.
    /// A region's <c>locations.json</c> response is typically 100&#8211;1500&#160;KB
    /// and serves quickly when the CDN is warm; defaulting to 60&#160;s
    /// gives generous headroom while still bounding hung calls.
    /// </summary>
    [Range(5, 600)]
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Directory under which per-region cache files are written
    /// (default <c>data/cache/pinballmap/</c>) — one file per region,
    /// named <c>locations-{region}.json</c>. Empty / whitespace
    /// disables the cache; every call hits the network. The cache
    /// stores the raw response body verbatim; freshness is judged by
    /// file modification time vs <see cref="CacheTtlSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Pinball Map publishes a <c>Crawl-delay: 3</c> directive in
    /// robots.txt. The cache eliminates the rate-limit problem for
    /// any repeat call within the TTL window — debug runs, dry-runs,
    /// and Wizard-citation lookups all share the same fetch.
    /// </remarks>
    public string CacheDirectory { get; set; } = "data/cache/pinballmap";

    /// <summary>
    /// Time-to-live for an on-disk region cache file, in seconds.
    /// Default 3600 (1&#160;hour) is a polite default that matches the
    /// site's general activity (locations are updated hourly at most
    /// across the whole map; per-region churn is much lower). Set to 0
    /// to force every call to bypass the cache (the file is still
    /// written on success, so a subsequent run with a non-zero TTL can
    /// use it).
    /// </summary>
    [Range(0, 86400)]
    public int CacheTtlSeconds { get; set; } = 3600;
}
