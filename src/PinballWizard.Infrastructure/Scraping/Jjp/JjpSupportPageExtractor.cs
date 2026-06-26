using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Jjp;

/// <summary>
/// Extracts per-edition support-page URLs and PDF document links from
/// Jersey Jack Pinball's support section. Pure functions — no I/O.
/// </summary>
/// <remarks>
/// <para>
/// JJP publishes a support index at <c>/support/</c> (Shopify custom
/// page) that lists every game edition with a link to its own support
/// sub-page at <c>/pages/support/{game-edition-slug}</c>.  Each
/// sub-page hosts per-edition PDF downloads: Game Manual, Rules
/// Flowchart, and (out-of-scope) Firmware and Changelog.
/// </para>
/// <para>
/// PDFs are served from JJP-owned CDN hosts:
/// <c>marketing.jerseyjackpinball.com</c> and
/// <c>downloadseu.jerseyjackpinball.com</c>, plus any same-host
/// (<c>jerseyjackpinball.com</c>) links.  Links to all three hosts are
/// accepted; firmware and changelog entries are skipped via
/// <see cref="IsSkippable"/>.
/// </para>
/// <para>
/// robots.txt at jerseyjackpinball.com has no Crawl-delay and no
/// restrictions on <c>/pages/support/</c> or <c>/support/</c>
/// (verified 2026-06-26).
/// </para>
/// </remarks>
public static class JjpSupportPageExtractor
{
    private static readonly HtmlParser Parser = new();

    // JJP-owned hosts from which support PDFs are served.
    // Verified from 2026-06-26 recon: marketing.jerseyjackpinball.com
    // (primary CDN) + downloadseu.jerseyjackpinball.com (EU mirror).
    private static readonly HashSet<string> JjpHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "jerseyjackpinball.com",
        "www.jerseyjackpinball.com",
        "marketing.jerseyjackpinball.com",
        "downloadseu.jerseyjackpinball.com",
    };

    // URL/text substrings that identify firmware, code-update, and
    // changelog entries — these are not RAG-relevant documents.
    private static readonly string[] SkipKeywords =
        ["firmware", "update", "changelog", "release note", "release_note", ".iso", ".zip"];

    // Edition suffixes that appear in JJP support URL slugs.
    // Ordered longest-first so overlapping suffixes don't partially match.
    private static readonly string[] EditionSuffixes =
    [
        "-collectors-edition",
        "-limited-edition",
        "-standard-edition",
        "-topper-edition",
        "-limited-le",
        "-le",
        "-se",
    ];

    /// <summary>
    /// Extracts the list of per-edition support page URLs from the JJP
    /// <c>/support/</c> index page HTML.
    /// </summary>
    /// <param name="html">Rendered HTML of the <c>/support/</c> index.</param>
    /// <param name="supportIndexUrl">
    /// Canonical URL of the support index; used to resolve relative hrefs.
    /// </param>
    /// <returns>
    /// Deduplicated list of absolute <c>/pages/support/…</c> URLs.
    /// </returns>
    public static List<Uri> ExtractSupportPageUrls(string html, Uri supportIndexUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(supportIndexUrl);

        using var doc = Parser.ParseDocument(html);
        var result = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(supportIndexUrl, href, out var abs)) continue;

            // Only accept /pages/support/* paths on the JJP main domain.
            if (!JjpHosts.Contains(abs.Host)) continue;
            if (!abs.AbsolutePath.StartsWith("/pages/support/", StringComparison.OrdinalIgnoreCase)) continue;

            var url = abs.GetLeftPart(UriPartial.Path);
            if (seen.Add(url))
                result.Add(abs);
        }

        return result;
    }

    /// <summary>
    /// Extracts PDF document links from a JJP per-edition support page.
    /// Firmware and changelog entries are skipped.
    /// </summary>
    /// <param name="html">Rendered HTML of the support sub-page.</param>
    /// <param name="pageUrl">
    /// Canonical URL of the support sub-page (used as discovery URL and to
    /// resolve relative hrefs).
    /// </param>
    /// <param name="gameSlug">
    /// Canonical game slug to attach to each discovered link.
    /// Derived from the support page URL slug by stripping edition suffixes
    /// via <see cref="DeriveGameSlug"/>.
    /// </param>
    /// <returns>
    /// Deduplicated list of <see cref="DiscoveredLink"/> values, one per
    /// unique PDF URL found on the page.
    /// </returns>
    public static List<DiscoveredLink> ExtractDocumentLinks(string html, Uri pageUrl, string gameSlug)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSlug);

        using var doc = Parser.ParseDocument(html);
        var links = new List<DiscoveredLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(pageUrl, href, out var abs)) continue;

            // Must be served from a JJP-owned host.
            if (!JjpHosts.Contains(abs.Host)) continue;

            // PDF files only.
            if (!abs.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

            var linkText = anchor.TextContent?.Trim() ?? string.Empty;

            // Skip firmware, code updates, and changelogs — not RAG documents.
            if (IsSkippable(linkText, abs.AbsolutePath)) continue;

            var url = abs.GetLeftPart(UriPartial.Path);
            if (!seen.Add(url)) continue;

            links.Add(new DiscoveredLink
            {
                FileUrl = url,
                LinkText = string.IsNullOrEmpty(linkText) ? null : linkText,
                DiscoveryContext = "JJP Support Page",
                GameSlug = gameSlug,
            });
        }

        return links;
    }

    /// <summary>
    /// Derives a canonical game slug from a JJP support page URL slug by
    /// stripping known edition suffixes.
    /// </summary>
    /// <example>
    /// "willy-wonka-the-chocolate-factory-limited-edition"
    ///     → "willy-wonka-the-chocolate-factory"
    /// "toy-story-4-collectors-edition"
    ///     → "toy-story-4"
    /// "godfather"
    ///     → "godfather" (no suffix — returned as-is)
    /// </example>
    public static string DeriveGameSlug(string supportPageSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supportPageSlug);

        foreach (var suffix in EditionSuffixes)
        {
            if (supportPageSlug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return supportPageSlug[..^suffix.Length];
        }

        return supportPageSlug;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static bool IsSkippable(string linkText, string urlPath)
    {
        var combined = linkText.ToLowerInvariant() + " " + urlPath.ToLowerInvariant();
        foreach (var kw in SkipKeywords)
        {
            if (combined.Contains(kw, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
