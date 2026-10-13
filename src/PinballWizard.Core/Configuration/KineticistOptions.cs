using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Kineticist tutorials scraper. Kineticist's founder
/// granted explicit written permission (ADR-0043 / PR #520) to index published
/// gameplay tutorials as Rulesheet documents via the <c>.md</c> URL suffix.
/// </summary>
/// <remarks>
/// The site exposes clean Markdown at <c>/news/{slug}.md</c>. Tutorials are
/// discovered from the news sitemap that robots.txt advertises, not by
/// paging the rendered category listing. The robots.txt (verified
/// 2026-09-28) sets <c>ai-train=yes</c>, allows <c>/news/</c> for all
/// crawlers, and specifies no <c>Crawl-delay</c>; pacing comes from the
/// <c>kineticist_tutorials</c> ingestion source's politeness overrides.
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
    /// Path to the news sitemap (a sitemaps.org <c>urlset</c> listing every
    /// <c>/news/{slug}</c> article), reachable from the sitemap index at
    /// <c>/sitemap.xml</c>. One cached request replaces paging
    /// <c>/news/category/pinball-tutorial?page=N</c>; tutorials are the entries
    /// whose slug carries a <c>tutorial</c> token (61 of 825 on 2026-09-28).
    /// </summary>
    [Required]
    public string NewsSitemapPath { get; set; } = "/sitemap/news.xml";

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
