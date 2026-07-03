using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Spooky Pinball scraper. Spooky runs WordPress
/// + WooCommerce + Yoast SEO and exposes a fully-open WordPress REST
/// API at <c>/wp-json/wp/v2/pages</c>, so the scraper extends
/// <c>PoliteScraperBase</c> and consumes JSON directly. No DOM
/// scraping, no Playwright.
/// </summary>
/// <remarks>
/// Phase 1.2.c of the parallel execution plan. Discovery is
/// machine-consumer-metadata-first per the locked feedback memory
/// <c>feedback_machine_consumer_metadata_first.md</c>: the WP REST API
/// returns the canonical title, slug, content, and modified-time as
/// structured JSON, which is more reliable and more polite than HTML
/// scraping.
/// <para>
/// A Spooky page is treated as a "game page" iff its content body
/// contains S3 firmware URLs that all share a single game slug
/// (e.g., a Beetlejuice page contains only
/// <c>spookypinball.s3.us-east-2.amazonaws.com/beetlejuice/...</c>
/// URLs). Aggregator / cross-game update pages reference multiple
/// slugs and are correctly excluded.
/// </para>
/// </remarks>
public sealed class SpookyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Spooky";

    /// <summary>Spooky root URL. Defaults to the production storefront.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.spookypinball.com";

    /// <summary>WordPress REST API pages endpoint path.</summary>
    public string PagesEndpointPath { get; set; } = "/wp-json/wp/v2/pages";

    /// <summary>Page size for WP REST pagination. Max 100.</summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// S3 bucket host that Spooky uses for firmware downloads. The
    /// presence of URLs at this host is the signal we use to identify
    /// game pages.
    /// </summary>
    public string S3Host { get; set; } = "spookypinball.s3.us-east-2.amazonaws.com";

    /// <summary>
    /// Defensive cap on the number of paginated WP REST page fetches
    /// per run. Spooky has roughly 30 pages today, so the default of
    /// 50 leaves plenty of headroom while still bounding a runaway
    /// loop if pagination is ever misconfigured.
    /// </summary>
    [Range(1, 1000)]
    public int MaxPagesToFetch { get; set; } = 50;

    /// <summary>
    /// WordPress page id of the "Game Support" hub page (slug: game-support).
    /// Child pages of this hub host per-game support PDFs in wp-content/uploads.
    /// Verified 2026-06-25 via WP REST: id=476 is the parent of halloween (1450),
    /// ultraman (1467), hwn-um-manual (1456), hwn-um-update-process (1453).
    /// </summary>
    /// <remarks>
    /// As of 2026-07, Spooky has migrated newer game manuals (Rick and Morty,
    /// ACNC, Scooby-Doo, Texas Chainsaw Massacre, Looney Tunes, Evil Dead,
    /// Beetlejuice) to PDF links embedded directly on the hub page rather than
    /// creating new WP child pages. Both discovery paths run on every scrape:
    /// WP REST child-page discovery (this ID) + direct hub-page HTML fetch
    /// (see <see cref="GameSupportHubPath"/>).
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int GameSupportParentPageId { get; set; } = 476;

    /// <summary>
    /// Relative path of the Game Support hub page. Fetched directly on every
    /// scrape run to discover PDF links embedded on the hub page itself —
    /// Spooky's current pattern for newer-title manuals (2022+). Complements
    /// the WP REST child-page discovery driven by <see cref="GameSupportParentPageId"/>.
    /// </summary>
    public string GameSupportHubPath { get; set; } = "/game-support/";
}
