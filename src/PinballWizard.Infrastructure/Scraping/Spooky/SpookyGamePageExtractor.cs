using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Spooky;

/// <summary>
/// Extracts a <see cref="GameRecord"/> and a list of
/// <see cref="DiscoveredLink"/> firmware assets from a Spooky WP REST
/// page object. Pure functions — no I/O.
/// </summary>
/// <remarks>
/// Spooky pages with WordPress slugs like <c>2486-2</c> have a real
/// title (<c>"Texas Chainsaw Massacre"</c>) but a meaningless slug. The
/// extractor prefers the S3 firmware URL's first path segment as the
/// canonical game slug (e.g. <c>texaschainsaw</c>) — that matches
/// Spooky's own internal naming and is stable across WP slug
/// renames.
/// </remarks>
public static class SpookyGamePageExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Extracts a <see cref="GameRecord"/> from a Spooky WP page.
    /// Returns null if the page doesn't pass the single-S3-slug check
    /// (i.e., it's an aggregator / non-game page).
    /// </summary>
    public static GameRecord? ExtractGame(SpookyPageRaw page, string s3Host)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(s3Host);

        var slugs = SpookyWpPagesClient.ExtractS3Slugs(page.Content.Rendered, s3Host);
        if (slugs.Count != 1) return null;
        var canonicalSlug = slugs.First();

        var title = WebUtility.HtmlDecode(page.Title.Rendered ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var pageUrl = page.Link;
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        return new GameRecord
        {
            GameId = $"game_spooky_{canonicalSlug}",
            Title = title,
            Slug = canonicalSlug,
            GamePageUrl = pageUrl,
            DiscoveredOn = ["spooky_wp_pages"],
            Source = new GameSourceInfo
            {
                ScrapedFrom = pageUrl,
                ScrapedAt = DateTime.UtcNow,
            },
        };
    }

    /// <summary>
    /// Extracts every S3-hosted firmware asset link from the page
    /// content. Spooky uses unusual extensions (<c>.pkg</c>,
    /// <c>.beetlejuice</c>, etc.) so the filter is by host rather than
    /// by extension.
    /// </summary>
    public static List<DiscoveredLink> ExtractDownloads(SpookyPageRaw page, string s3Host)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(s3Host);

        var links = new List<DiscoveredLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var slugs = SpookyWpPagesClient.ExtractS3Slugs(page.Content.Rendered, s3Host);
        var canonicalSlug = slugs.Count == 1 ? slugs.First() : null;
        if (canonicalSlug is null) return links;

        var html = page.Content.Rendered ?? string.Empty;
        if (string.IsNullOrEmpty(html)) return links;

        // Anchor-text lookup so we can attach a human-readable label
        // (release-notes heading, version label, etc.) to each link.
        var anchorTextByHref = BuildAnchorTextLookup(html);

        var pattern = @"https?://" + Regex.Escape(s3Host) + @"/[^\s""'<>]+";
        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
        {
            var url = WebUtility.HtmlDecode(match.Value);
            if (!seenUrls.Add(url)) continue;

            anchorTextByHref.TryGetValue(url, out var linkText);

            links.Add(new DiscoveredLink
            {
                FileUrl = url,
                LinkText = string.IsNullOrWhiteSpace(linkText) ? null : linkText,
                DiscoveryContext = "Spooky Pinball Game Page",
                GameSlug = canonicalSlug,
            });
        }

        return links;
    }

    private static Dictionary<string, string> BuildAnchorTextLookup(string html)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = Parser.ParseDocument(html);
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                var text = anchor.TextContent?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                var decoded = WebUtility.HtmlDecode(href);
                map.TryAdd(decoded, text);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException
                                       or FormatException or System.Text.Json.JsonException)
        {
            // Anchor lookup is best-effort — a parse failure must not
            // block download discovery; the regex pass already captured
            // the URLs we care about. OOM/cancellation still propagate.
        }
        return map;
    }
}
