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
/// at <c>/sitemap.xml</c> exists and returns 200, but is incomplete:
/// it omitted Pulp Fiction and Cactus Canyon as of 2026-05 and STILL
/// omits both as of 2026-09-05 (192 entries, three coin-op machines,
/// neither string present anywhere in the file). So discovery uses the
/// <see cref="MachinesIndexPath"/> page instead. That page's navigation
/// links every shipped machine and is the canonical filter — the same ROLE as
/// the Barrels of Fun category endpoint (a canonical list of shipped machines
/// used to drive the per-page fetch), though the mechanism differs: BoF queries
/// the WooCommerce Store API by category id, CGC scans anchors out of HTML.
/// It was <c>/coinop/</c> until CGC retired that index (#967); it is now the
/// site root.
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
    /// completes its sitemap (it exists but omits two of the five machines — see the
    /// class remarks) or publishes a real machine listing, prefer it.
    /// </para>
    /// <para>
    /// The yield guard covers TOTAL discovery loss only. CGC has no entry in
    /// <c>Scraper:MinimumYieldPerScraper</c>, so its minimum is the default of 1: a
    /// run collecting nothing exits non-zero, but a nav reshuffle that drops two of
    /// five titles yields 3, clears the minimum, and passes silently. That partial
    /// case is an accepted, unguarded risk — pinning an exact count would false-alarm
    /// whenever CGC genuinely retires a machine.
    /// </para>
    /// </para>
    /// </remarks>
    [Required]
    public string MachinesIndexPath { get; set; } = "/";

    /// <summary>
    /// URL path prefix that identifies a CGC machine page. URLs
    /// whose absolute path begins with this prefix AND have exactly
    /// one segment after it are treated as machines. Sub-pages are excluded by that
    /// rule rather than by enumeration — <c>/coinop/{slug}/update</c>,
    /// <c>/coinop/{slug}/update/mac</c> and <c>/coinop/{slug}/upgrade</c> are the
    /// shapes seen so far, and the rule catches any other.
    /// </summary>
    [Required]
    public string GamePathPrefix { get; set; } = "/coinop/";
}
