using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the American Pinball (AP) scraper. AP is a
/// WordPress site (Yoast sitemap index, server-rendered HTML), so the
/// scraper extends <c>PoliteScraperBase</c> and uses HttpClient +
/// AngleSharp.
/// </summary>
/// <remarks>
/// Discovery is sitemap-first per the locked feedback memory
/// <c>feedback_machine_consumer_metadata_first.md</c>. The live site
/// (Yoast SEO on WordPress) serves <see cref="SitemapPath"/> as a
/// sitemap index; game pages are root permalinks
/// (<c>/{slug}/</c>) classified by the <see cref="GamePageCategorySlug"/>
/// category. <see cref="GamePathPrefix"/> remains the legacy
/// <c>/games/{slug}</c> filter for a flat urlset.
/// </remarks>
public sealed class ApOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ap";

    /// <summary>AP root URL. Defaults to the production storefront.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.american-pinball.com";

    /// <summary>
    /// Sitemap path. Yoast redirects this to a sitemap index;
    /// a legacy flat urlset is still accepted.
    /// </summary>
    public string SitemapPath { get; set; } = "/sitemap.xml";

    /// <summary>
    /// Path prefix that identifies legacy game pages. URLs whose
    /// absolute path begins with this prefix and has exactly one
    /// slug segment after it are treated as game pages.
    /// </summary>
    public string GamePathPrefix { get; set; } = "/games/";

    /// <summary>
    /// Hosts a child sitemap, game permalink, or support page may use. The Yoast
    /// index lives on <c>americanpinball.com</c> after the
    /// <c>www.american-pinball.com</c> redirect, so both names are
    /// required. Any other host in the index is skipped.
    /// </summary>
    public string[] SitemapHosts { get; set; } =
    [
        "www.american-pinball.com",
        "american-pinball.com",
        "www.americanpinball.com",
        "americanpinball.com",
    ];

    /// <summary>WordPress category slug whose posts are game pages.</summary>
    [Required]
    public string GamePageCategorySlug { get; set; } = "game-page";

    /// <summary>WordPress REST categories collection path.</summary>
    [Required]
    public string CategoriesEndpointPath { get; set; } = "/wp-json/wp/v2/categories";

    /// <summary>WordPress REST posts collection path.</summary>
    [Required]
    public string PostsEndpointPath { get; set; } = "/wp-json/wp/v2/posts";

    /// <summary>Page size for the game-page category query. WordPress caps this at 100.</summary>
    [Range(1, 100)]
    public int CategoryPageSize { get; set; } = 100;

    /// <summary>Safety cap on category pagination. A full page at the cap is logged.</summary>
    [Range(1, 20)]
    public int MaxCategoryPages { get; set; } = 5;

    /// <summary>WordPress REST pages collection path.</summary>
    [Required]
    public string PagesEndpointPath { get; set; } = "/wp-json/wp/v2/pages";

    /// <summary>
    /// Slug of the top-level support page. Its WordPress child pages
    /// are the per-game support hubs (<c>/support/{game-slug}/</c>)
    /// that publish the service bulletins.
    /// </summary>
    [Required]
    public string SupportPageSlug { get; set; } = "support";

    /// <summary>
    /// Child pages of the support page that are known not to be
    /// per-game hubs. Any other child whose slug is not a game-page
    /// slug is logged as a warning: it may be a new game's hub whose
    /// game page is not categorized yet.
    /// </summary>
    [Required]
    public string[] NonGameSupportPageSlugs { get; set; } = ["register", "updates"];

    /// <summary>
    /// WordPress post categories whose cards on a per-game support
    /// page are service bulletins. <c>electrical</c> holds the
    /// install / retrofit guides that the pre-redesign support page
    /// listed alongside the bulletins.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string[] BulletinCategorySlugs { get; set; } = ["service-bulletin", "electrical"];
}
