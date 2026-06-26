using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Kineticist tutorials scraper. Kineticist's founder
/// granted explicit written permission (ADR-0043 / PR #520) to index published
/// gameplay tutorials as Rulesheet documents via the <c>.md</c> URL suffix.
/// </summary>
/// <remarks>
/// The site exposes clean Markdown at <c>/news/{slug}.md</c>; the tutorial
/// listing is paged at <c>/news/category/pinball-tutorial?page=N</c>.
/// The robots.txt (verified 2026-06-25) sets <c>ai-train=yes</c> and
/// allows <c>/news/</c> for all crawlers including <c>ClaudeBot</c>.
/// No <c>Crawl-delay</c> is specified; politeness defaults apply.
/// </remarks>
public sealed class KineticistOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kineticist";

    /// <summary>Kineticist root URL.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.kineticist.com";

    /// <summary>
    /// Path to the paginated tutorial category listing.
    /// Articles are discovered from <c>/news/category/pinball-tutorial?page=N</c>.
    /// </summary>
    public string TutorialCategoryPath { get; set; } = "/news/category/pinball-tutorial";

    /// <summary>
    /// Defensive cap on the number of category pages to fetch per run.
    /// The catalogue has ~50 articles across 2 pages as of 2026-06-25;
    /// default of 20 leaves headroom without bounding a runaway loop.
    /// </summary>
    [Range(1, 200)]
    public int MaxCategoryPagesToFetch { get; set; } = 20;
}
