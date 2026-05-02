using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the American Pinball (AP) scraper. AP is a
/// server-rendered HTML site (custom CMS, not Shopify), so the
/// scraper extends <c>PoliteScraperBase</c> and uses HttpClient +
/// AngleSharp.
/// </summary>
/// <remarks>
/// Phase 1.2.b of the parallel execution plan. Discovery is
/// sitemap-first per the locked feedback memory
/// <c>feedback_machine_consumer_metadata_first.md</c>; AP's sitemap
/// is a flat urlset (not a paginated index), so a single fetch
/// returns every URL.
/// </remarks>
public sealed class ApOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ap";

    /// <summary>AP root URL. Defaults to the production storefront.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.american-pinball.com";

    /// <summary>Sitemap path. Flat urlset (not an index).</summary>
    public string SitemapPath { get; set; } = "/sitemap.xml";

    /// <summary>
    /// Path prefix that identifies game pages. URLs whose absolute
    /// path begins with this prefix are treated as game pages by the
    /// sitemap discovery filter.
    /// </summary>
    public string GamePathPrefix { get; set; } = "/games/";
}
