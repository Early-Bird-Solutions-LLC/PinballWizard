namespace PinballWizard.Infrastructure.Scraping.Kineticist;

/// <summary>
/// A single Kineticist tutorial article discovered and fetched during a scrape run.
/// </summary>
/// <remarks>
/// <para>
/// The <c>.md</c> suffix on any Kineticist article URL returns clean Markdown
/// (verified 2026-06-25). The Markdown body includes the title, author, publish
/// date, category, and canonical URL as structured front-matter-style lines,
/// followed by the full article body as Markdown prose.
/// </para>
/// <para>
/// <see cref="GameSlug"/> is derived by stripping the <c>-pinball-tutorial</c>,
/// <c>-tutorial</c>, and similar suffixes from the article URL slug so it can
/// be used to look up the corresponding machine via
/// <c>IMachineTitleLookupRepository</c>.
/// </para>
/// </remarks>
public sealed class KineticistTutorialArticle
{
    /// <summary>
    /// Editorial headline from the article Markdown (e.g. "Autobots, Transform and Roll Out!").
    /// This is NOT the game name.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>Author as published in the article Markdown (e.g. "Noah Crable").</summary>
    public required string Author { get; init; }

    /// <summary>
    /// Canonical article URL (e.g. "https://www.kineticist.com/news/transformers-pinball-tutorial").
    /// This is the citation URL that rides every RAG answer sourced from this article.
    /// </summary>
    public required string CanonicalUrl { get; init; }

    /// <summary>
    /// Game slug derived from the article URL slug (e.g. "transformers").
    /// Used to look up the corresponding machine record in the OPDB catalog.
    /// </summary>
    public required string GameSlug { get; init; }

    /// <summary>
    /// Full Markdown body of the article as returned by the <c>.md</c> endpoint.
    /// Already structured text — no PDF extraction needed.
    /// </summary>
    public required string MarkdownContent { get; init; }

    /// <summary>Publish date parsed from the article Markdown, if present.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
}
