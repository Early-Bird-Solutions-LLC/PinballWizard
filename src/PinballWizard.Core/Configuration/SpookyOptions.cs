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
}
