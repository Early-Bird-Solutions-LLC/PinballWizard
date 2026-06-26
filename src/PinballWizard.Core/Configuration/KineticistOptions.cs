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

    /// <summary>
    /// Base URL of the Kineticist public API (v1). The games catalog is
    /// OPDB-keyed (ADR-0043 Tier A): each game detail at
    /// <c>{ApiBaseUrl}/games/{slug}</c> carries an <c>editions[]</c> array
    /// whose <c>opdb_id</c> values join directly to our OPDB-keyed machine
    /// catalog. Title search is <c>{ApiBaseUrl}/games?q={terms}</c>.
    /// Verified 2026-06-26.
    /// </summary>
    [Required]
    [Url]
    public string ApiBaseUrl { get; set; } = "https://www.kineticist.com/api/v1";

    /// <summary>
    /// Bearer API key for the Kineticist API (a <c>ki_live_</c> token granted
    /// by the operator per ADR-0043). Secret: sourced from Key Vault in
    /// production and from the <c>KINETICIST_API_KEY</c> environment variable
    /// (mapped to <c>Kineticist:ApiKey</c>) locally. When empty, the
    /// OPDB-keyed API linking path is not registered and the tutorials sync
    /// falls back to title-lookup linking — degrade visibly, never silently.
    /// </summary>
    public string? ApiKey { get; set; }
}
