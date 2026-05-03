using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Multimorphic scraper. Multimorphic runs
/// WordPress + WooCommerce; the storefront enumerates ~100 products
/// across game kits, third-party game kits, circuit boards,
/// accessories, artwork, and apparel. We scrape only the
/// Multimorphic-published P3 game kits.
/// </summary>
/// <remarks>
/// Phase 1.3 of the manufacturer-scraper fan-out. Discovery is
/// sitemap-first per the locked feedback memory
/// <c>feedback_machine_consumer_metadata_first.md</c>. The site uses
/// a WordPress sitemap index (<c>/wp-sitemap.xml</c>) referencing
/// per-type sub-sitemaps; we follow only the product sitemap and
/// then filter to URLs whose path begins with
/// <see cref="MultimorphicGameKitsPathPrefix"/>.
/// <para>
/// Third-party P3 game kits (Drained, Princess Bride, Portal, etc.)
/// share the storefront but belong to their respective studios;
/// the OPDB sync attributes them to those studios, so the
/// Multimorphic scraper deliberately excludes them — running with
/// <c>manufacturer = multimorphic</c> would land them in the wrong
/// Cosmos partition.
/// </para>
/// </remarks>
public sealed class MultimorphicOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Multimorphic";

    /// <summary>Storefront root URL.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.multimorphic.com";

    /// <summary>Sitemap index path (WordPress-standard).</summary>
    [Required]
    public string SitemapPath { get; set; } = "/wp-sitemap.xml";

    /// <summary>
    /// Path prefix that identifies a Multimorphic-published P3 game
    /// kit. URLs whose absolute path begins with this value are
    /// treated as machines; everything else (third-party kits,
    /// circuit boards, parts) is excluded.
    /// </summary>
    [Required]
    public string MultimorphicGameKitsPathPrefix { get; set; }
        = "/store/p3-game-kits/multimorphic-game-kits/";
}
