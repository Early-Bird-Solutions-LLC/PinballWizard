using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Politeness invariants applied to every scraper that extends
/// <c>PoliteScraperBase</c> or <c>PolitePlaywrightScraperBase</c>. Per
/// the locked feedback memory <c>feedback_polite_scraping.md</c>: we
/// scrape third-party sites and the project's professional ethics
/// require these defaults to be visibly enforced, not relied on by
/// convention.
/// </summary>
/// <remarks>
/// Bound from <c>appsettings.json</c> section <c>"Politeness"</c>.
/// Per-source overrides land in the <c>IngestionSource</c> Cosmos
/// document (per ADR 0007); the gate consults overrides at
/// request-acquire time and falls back to these defaults when an
/// override field is null.
/// </remarks>
public sealed class PolitenessOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Politeness";

    /// <summary>
    /// Descriptive User-Agent identifying the project AND linking back
    /// to the repository so source-site operators can identify and
    /// contact us. Per the polite-scraping ethos, this is intentionally
    /// transparent rather than mimicking a real browser.
    /// </summary>
    [Required]
    public string UserAgent { get; set; } =
        "PinballWizard/0.1 (+https://github.com/Early-Bird-Solutions-LLC/PinballWizard; polite-scraper)";

    /// <summary>
    /// Minimum delay in milliseconds between two requests to the same
    /// origin. Floor: 250 ms. Default: 2000 ms (matches the previous
    /// <c>PageLoadDelayMs</c>). Per-source overrides may go higher but
    /// not lower than the floor.
    /// </summary>
    [Range(250, 60_000)]
    public int RequestDelayMs { get; set; } = 2_000;

    /// <summary>Upper bound of <see cref="Max429Streak"/>; also the hard cap on 429 re-sends per request.</summary>
    public const int Max429StreakUpperBound = 10;

    /// <summary>
    /// Maximum number of consecutive HTTP 429 responses tolerated from
    /// a single source before the gate throws and aborts the scrape.
    /// </summary>
    [Range(1, Max429StreakUpperBound)]
    public int Max429Streak { get; set; } = 3;

    /// <summary>
    /// Base backoff in milliseconds applied to an origin after a 429 that
    /// carries no <c>Retry-After</c> header. Doubles with each consecutive
    /// 429 from the same origin and is capped at <see cref="MaxRetryAfterSeconds"/>.
    /// </summary>
    [Range(1_000, 600_000)]
    public int RateLimitBackoffMs { get; set; } = 30_000;

    /// <summary>
    /// Longest wait a single 429 may impose on an origin. A server-sent
    /// <c>Retry-After</c> longer than this aborts the run instead of
    /// sleeping (the source is asking us to come back later than the run's
    /// budget allows); computed backoff is capped at this value.
    /// </summary>
    [Range(1, 3_600)]
    public int MaxRetryAfterSeconds { get; set; } = 600;

    /// <summary>If true, every request URL is checked against the host's robots.txt before being issued.</summary>
    public bool RespectRobotsTxt { get; set; } = true;

    /// <summary>Path to the robots.txt file (relative to the host root).</summary>
    public string RobotsTxtPath { get; set; } = "/robots.txt";

    /// <summary>How long a parsed robots.txt is cached per host. Defaults to one hour.</summary>
    [Range(60, 86_400)]
    public int RobotsTxtTtlSeconds { get; set; } = 3_600;
}
