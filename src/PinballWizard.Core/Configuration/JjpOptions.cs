using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Jersey Jack Pinball (JJP) scraper.
/// JJP runs on Shopify, so the site is server-rendered HTML — no
/// browser automation required, just polite HttpClient use.
/// </summary>
/// <remarks>
/// Phase 1.2 of the parallel execution plan. Discovery is sitemap-first
/// (per the locked feedback memory <c>feedback_machine_consumer_metadata_first.md</c>):
/// we read JJP's Shopify-generated sitemap index, follow product
/// sitemaps, and only fetch the per-product pages that match the
/// pinball-machine collection. Per-product extraction prefers
/// JSON-LD product schema and Open Graph tags over rendered-DOM
/// scraping.
/// </remarks>
public sealed class JjpOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Jjp";

    /// <summary>JJP root URL. Defaults to the production storefront.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://jerseyjackpinball.com";

    /// <summary>Sitemap index path (Shopify-standard).</summary>
    public string SitemapPath { get; set; } = "/sitemap.xml";

    /// <summary>
    /// Collection slug for pinball machines. Used to filter the
    /// product sitemap down to actual machines — JJP's storefront
    /// includes apparel / accessories / banners that share the
    /// <c>/products/</c> URL space and would otherwise pollute the
    /// machine catalog. The scraper fetches
    /// <c>/collections/{slug}/products.json</c> and intersects the
    /// resulting handle set with the sitemap URLs.
    /// </summary>
    [Required]
    public string PinballMachinesCollectionSlug { get; set; } = "pinball-machines-for-sale";
}
