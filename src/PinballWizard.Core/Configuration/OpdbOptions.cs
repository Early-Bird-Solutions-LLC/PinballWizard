using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the OPDB (Open Pinball Database) integration —
/// the canonical machine catalog the project uses to identify pinball
/// machines across all manufacturer sources.
/// </summary>
/// <remarks>
/// Since 2026-10-01 the opdb.org <c>/api/*</c> surface is shut down.
/// Bulk catalog sync reads the Match Play OPDB V1 CDN export
/// (<see cref="ExportUrl"/>); optional per-entry lookups use the Match
/// Play API at <see cref="BaseUrl"/>. See ADR-0059.
/// <para>
/// API token: Match Play bearer credential for optional entry/changelog
/// endpoints. The CDN export itself is public and does not require a
/// token. Set via <c>OPDB__APITOKEN</c> / Key Vault <c>Opdb-ApiToken</c>
/// when entry API calls are needed; never commit the token.
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

    /// <summary>
    /// Match Play API base URL used for optional per-entry / changelog
    /// requests (e.g. group-title lookup when the export has not yet
    /// been parsed this process). Defaults to the public Match Play API.
    /// </summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://api.matchplay.events/";

    /// <summary>
    /// Absolute URL of the Match Play OPDB V1 bulk export JSON.
    /// This is the sole catalog source for <c>StreamAllMachinesAsync</c>
    /// — there is no fallback to the retired opdb.org <c>/api/export</c>.
    /// </summary>
    [Required]
    [Url]
    public string ExportUrl { get; set; } =
        "https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-opdb.json";

    /// <summary>
    /// Absolute URL of the Match Play OPDB V2 export. Used only to copy
    /// design credits and the primary backglass onto machines already
    /// mapped from <see cref="ExportUrl"/>. Empty skips that pass.
    /// A configured URL that fails still fails the sync.
    /// </summary>
    public string V2ExportUrl { get; set; } =
        "https://mp-data.sfo3.cdn.digitaloceanspaces.com/opdb-v2.json";

    /// <summary>
    /// Bearer token for Match Play API requests. Not sent on CDN export
    /// fetches (public). Empty is allowed for export-only sync runs.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Total HTTP timeout in seconds. Governs both the
    /// <see cref="HttpClient.Timeout"/> on the typed OPDB client and the
    /// <c>TotalRequestTimeout</c> on the OPDB-specific resilience handler.
    /// The Match Play OPDB V1 export is ~3.5&#160;MB and usually returns in
    /// under a second from the CDN; 120s still bounds hung calls.
    /// </summary>
    [Range(5, 600)]
    public int HttpTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// On-disk cache path for the Match Play OPDB V1 export response. Set
    /// to a relative path under the project root (default
    /// <c>data/cache/opdb-export.json</c>) or an absolute path for
    /// production deployments. Empty / whitespace disables the cache —
    /// every call hits the network. The cache stores the raw response
    /// body verbatim; freshness is judged by file modification time vs
    /// <see cref="ExportCacheTtlSeconds"/>.
    /// </summary>
    /// <remarks>
    /// The CDN export updates about once per day and advertises
    /// <c>Cache-Control: max-age=3600</c>. Default TTL of 1 hour matches
    /// that hint and avoids repeat CDN hits during dry-run / debug /
    /// apply sequences in the same hour.
    /// </remarks>
    public string ExportCachePath { get; set; } = "data/cache/opdb-export.json";

    /// <summary>
    /// Time-to-live for the on-disk export cache, in seconds. Default
    /// 3600 (1 hour). Set to 0 to force every call to bypass the cache
    /// (the file is still written on success, so a subsequent run with a
    /// non-zero TTL can use it).
    /// </summary>
    [Range(0, 86400)]
    public int ExportCacheTtlSeconds { get; set; } = 3600;

    /// <summary>
    /// On-disk cache path for group-title resolutions (segment → title).
    /// Populated primarily from the export's <c>machineGroups</c> array
    /// during <c>StreamAllMachinesAsync</c>; optional Match Play entry
    /// GETs fill gaps only when the export has not been loaded this
    /// process. Empty / whitespace disables the cache.
    /// </summary>
    public string GroupTitleCachePath { get; set; } = "data/cache/opdb-group-titles.json";

    /// <summary>
    /// Time-to-live for the group-title cache file, in seconds. Default
    /// 1 209 600 (14 days). The whole cache file expires by mtime.
    /// Set to 0 to force every call to bypass the cache (new entries are
    /// still written on success).
    /// </summary>
    [Range(0, int.MaxValue)]
    public int GroupTitleCacheTtlSeconds { get; set; } = 1_209_600; // 14 days
}
