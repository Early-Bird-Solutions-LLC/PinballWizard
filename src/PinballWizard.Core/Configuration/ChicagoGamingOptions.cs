using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Chicago Gaming Company (CGC) scraper. CGC's
/// site is a custom Nginx-served HTML stack (no WordPress, no
/// Shopify, no SPA), so the scraper extends <c>PoliteScraperBase</c>
/// and uses HttpClient + AngleSharp.
/// </summary>
/// <remarks>
/// Phase 1.3 of the manufacturer-scraper fan-out. The CGC sitemap
/// at <c>/sitemap.xml</c> is incomplete in practice (omits Pulp
/// Fiction and Cactus Canyon as of 2026-05) so discovery uses the
/// <see cref="MachinesIndexPath"/> page instead. That page's navigation
/// links every shipped machine and is the canonical filter — same pattern as
/// Barrels of Fun's <c>/product-category/machines/</c>. It was <c>/coinop/</c>
/// until CGC retired that index (#967); it is now the site root.
/// <para>
/// CGC produces "Remake" editions of classic Bally/Williams pinball
/// machines (Attack from Mars, Medieval Madness, Monster Bash,
/// Cactus Canyon, Pulp Fiction). The OPDB key matching
/// <c>OpdbMachineMapper.NormalizeManufacturerKey</c> is <c>cgc</c>.
/// </para>
/// </remarks>
public sealed class ChicagoGamingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ChicagoGaming";

    /// <summary>
    /// CGC root URL. The site requires the <c>www</c> subdomain;
    /// the bare apex returns a 301 redirect.
    /// </summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.chicago-gaming.com";

    /// <summary>
    /// Path to the page whose anchors are scanned for machines.
    /// Discovery extracts <c>/coinop/{slug}</c> anchors from this page.
    /// </summary>
    /// <remarks>
    /// The site root, not <c>/coinop/</c>. CGC retired the dedicated machines index
    /// around 2026-08-23 — it now returns 404 while the root returns 200 and each
    /// <c>/coinop/{slug}</c> page still resolves, which failed every scheduled CGC
    /// scrape for a fortnight (#967). The root's navigation links every shipped
    /// coin-op title, and <see cref="GamePathPrefix"/> plus the single-slug-segment
    /// rule still does the filtering, so only the fetch target changed.
    /// <para>
    /// This is a navigation source rather than a dedicated index, so it is more
    /// fragile than what it replaced: a nav reshuffle changes discovery. If CGC ever
    /// publishes a real machine listing or a sitemap (neither existed when this was
    /// captured), prefer it. The yield guard is what catches the next break.
    /// </para>
    /// </remarks>
    [Required]
    public string MachinesIndexPath { get; set; } = "/";

    /// <summary>
    /// URL path prefix that identifies a CGC machine page. URLs
    /// whose absolute path begins with this prefix AND have exactly
    /// one segment after it are treated as machines; sub-pages like
    /// <c>/coinop/{slug}/update</c> and
    /// <c>/coinop/{slug}/update/mac</c> are excluded.
    /// </summary>
    [Required]
    public string GamePathPrefix { get; set; } = "/coinop/";
}
