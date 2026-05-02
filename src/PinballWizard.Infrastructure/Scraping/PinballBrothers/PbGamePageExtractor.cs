using System.Net;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// Extracts a <see cref="GameRecord"/> from a Pinball Brothers WP
/// REST page object. Pure functions — no I/O.
/// </summary>
/// <remarks>
/// Pinball Brothers' game pages are pure marketing — Visual Composer
/// shortcodes for tabs, no firmware downloads, no JSON-LD product
/// schema, no pricing in any machine-consumer-friendly form.
/// Edition data is technically embedded in shortcode <c>[vc_tta_section
/// title="..."]</c> attributes but extracting them reliably needs a
/// shortcode parser. For v1 the scraper produces a minimal
/// <see cref="GameRecord"/> (title + canonical slug + page URL) and
/// the catalog spine comes from OPDB, matching the AP and Spooky
/// patterns. Edition extraction can land in a follow-up if it's worth
/// the parser.
/// </remarks>
public static class PbGamePageExtractor
{
    /// <summary>
    /// Extracts a <see cref="GameRecord"/> from a Pinball Brothers WP
    /// page. Returns null if title or page URL are missing, or if the
    /// slug does not end with <paramref name="slugSuffix"/>.
    /// </summary>
    public static GameRecord? ExtractGame(PbPageRaw page, string slugSuffix)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(slugSuffix);

        if (!HasGameSuffix(page.Slug, slugSuffix)) return null;
        if (string.IsNullOrWhiteSpace(page.Link)) return null;

        var canonicalSlug = StripSuffix(page.Slug, slugSuffix);
        if (string.IsNullOrWhiteSpace(canonicalSlug)) return null;

        var title = WebUtility.HtmlDecode(page.Title.Rendered ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        return new GameRecord
        {
            GameId = $"game_pinballbrothers_{canonicalSlug}",
            Title = title,
            Slug = canonicalSlug,
            GamePageUrl = page.Link,
            DiscoveredOn = ["pinballbrothers_wp_pages"],
            Source = new GameSourceInfo
            {
                ScrapedFrom = page.Link,
                ScrapedAt = DateTime.UtcNow,
            },
        };
    }

    /// <summary>
    /// Strips a trailing <paramref name="suffix"/> from
    /// <paramref name="slug"/> case-insensitively. Returns the empty
    /// string if the slug equals the suffix exactly.
    /// </summary>
    public static string StripSuffix(string slug, string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        ArgumentException.ThrowIfNullOrEmpty(suffix);

        if (!HasGameSuffix(slug, suffix)) return slug;
        return slug[..^suffix.Length];
    }

    private static bool HasGameSuffix(string? slug, string suffix) =>
        !string.IsNullOrEmpty(slug)
        && slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}
