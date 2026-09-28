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
/// at <c>/sitemap.xml</c> is a 2019 generator snapshot that omits
/// Pulp Fiction and Cactus Canyon (re-verified 2026-09-28), so
/// discovery reads the machine links from the <see cref="MachinesIndexPath"/>
/// page instead. The dedicated <c>/coinop/</c> index returned 404 from
/// August 2026 (#967); the site root's header navigation is now the
/// only page that lists every coin-op machine.
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
    /// Path to the page whose links list every CGC machine. The site
    /// root carries them in its "Pinball" header menu. Discovery
    /// extracts <c>/coinop/{slug}</c> anchors from this page.
    /// </summary>
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
