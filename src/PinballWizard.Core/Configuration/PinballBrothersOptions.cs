using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Pinball Brothers scraper. The site runs
/// WordPress + Visual Composer with the WP REST API fully open at
/// <c>/wp-json/wp/v2/pages</c>, so the scraper consumes JSON
/// directly — no DOM scraping, no Playwright.
/// </summary>
/// <remarks>
/// Phase 1.3 of the manufacturer-scraper fan-out. Pages whose slug
/// ends with <see cref="GameSlugSuffix"/> are treated as game pages
/// (e.g., <c>queen-pinball</c>, <c>alien-pinball</c>). The suffix is
/// stripped to derive the canonical game slug used in
/// <c>GameRecord.Slug</c> and <c>Machine.ManufacturerSlugs</c>.
/// </remarks>
public sealed class PinballBrothersOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "PinballBrothers";

    /// <summary>Pinball Brothers root URL.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.pinballbrothers.com";

    /// <summary>WordPress REST API pages endpoint path.</summary>
    public string PagesEndpointPath { get; set; } = "/wp-json/wp/v2/pages";

    /// <summary>Page size for WP REST pagination. Max 100.</summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Defensive cap on the number of paginated WP REST page fetches
    /// per run. The site has &lt;20 pages today, so the default of 50
    /// leaves headroom while still bounding a runaway loop if
    /// pagination is ever misconfigured.
    /// </summary>
    [Range(1, 1000)]
    public int MaxPagesToFetch { get; set; } = 50;

    /// <summary>
    /// Slug suffix that identifies a page as a game page. Pages whose
    /// slug ends with this string are treated as games; the suffix is
    /// stripped to derive the canonical slug.
    /// </summary>
    [Required]
    public string GameSlugSuffix { get; set; } = "-pinball";
}
