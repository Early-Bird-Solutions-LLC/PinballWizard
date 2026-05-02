using System.Globalization;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Stern;

/// <summary>
/// Pure helpers for post-processing raw DOM extractions from <see cref="GamePageScraper"/>.
/// Kept separate so they can be unit tested without spinning up Playwright.
/// </summary>
public static class GamePageExtractors
{
    private static readonly string[] BannerKeywords =
    {
        "cookie",
        "privacy",
        "consent",
        "your privacy choices",
        "manage preferences",
        "skip to content",
        // CTA / signup widgets — Stern templates a per-game H1 onto its
        // newsletter widget (e.g. "Sign up for Pokémon by Stern Pinball Updates!"),
        // which would otherwise win the candidate race against the real game H1.
        "sign up for",
        "subscribe",
        "newsletter",
    };

    // Stern's <title> / templated H1 suffix. Both pipe and dash variants are
    // observed in the wild; strip whichever is present.
    private static readonly string[] SternTitleSuffixes =
    {
        " | Stern Pinball",
        " - Stern Pinball",
    };

    /// <summary>
    /// Picks the best title from raw page candidates, falling back to a cleaned
    /// <c>document.title</c> and finally a slug-cased title.
    /// Rejects banner/menu/signup text (cookie consent, privacy choices, "Sign up
    /// for X Updates!", etc.) which otherwise win the H1 race against the real
    /// game heading. Strips trailing " | Stern Pinball" / " - Stern Pinball"
    /// from whichever candidate or page-title fallback is selected.
    /// </summary>
    public static string SanitizeGameTitle(
        IReadOnlyList<string?>? candidates,
        string? pageTitle,
        string slug)
    {
        if (candidates is not null)
        {
            foreach (var candidate in candidates)
            {
                var cleaned = StripSternSuffix(candidate?.Trim());
                if (IsValidTitle(cleaned))
                {
                    return cleaned!;
                }
            }
        }

        var stripped = StripSternSuffix(pageTitle);
        if (IsValidTitle(stripped))
        {
            return stripped!;
        }

        return SlugToTitle(slug);
    }

    /// <summary>
    /// Collapses duplicate editions by lowercased name, merging non-null fields.
    /// Stern's edition cards are wrapped in nested elements that all match
    /// <c>[class*="edition"]</c>, so the JS extraction returns each edition 3x.
    /// First occurrence wins for ordering; later occurrences only fill gaps.
    /// </summary>
    public static List<EditionInfo> DeduplicateEditions(IEnumerable<EditionInfo> editions)
    {
        var byKey = new Dictionary<string, EditionInfo>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var ed in editions)
        {
            var name = ed.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var key = name.ToLowerInvariant();
            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = new EditionInfo
                {
                    Name = name,
                    Msrp = NullIfBlank(ed.Msrp),
                    Description = NullIfBlank(ed.Description),
                    Availability = NullIfBlank(ed.Availability),
                    LimitedQuantity = ed.LimitedQuantity,
                    UniqueFeatures = [.. ed.UniqueFeatures],
                    ImageUrls = [.. ed.ImageUrls],
                };
                order.Add(key);
            }
            else
            {
                existing.Msrp ??= NullIfBlank(ed.Msrp);
                existing.Description ??= NullIfBlank(ed.Description);
                existing.Availability ??= NullIfBlank(ed.Availability);
                existing.LimitedQuantity ??= ed.LimitedQuantity;

                foreach (var feature in ed.UniqueFeatures)
                {
                    if (!existing.UniqueFeatures.Contains(feature))
                        existing.UniqueFeatures.Add(feature);
                }

                foreach (var url in ed.ImageUrls)
                {
                    if (!existing.ImageUrls.Contains(url))
                        existing.ImageUrls.Add(url);
                }
            }
        }

        return [.. order.Select(k => byKey[k])];
    }

    private static bool IsValidTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        foreach (var banned in BannerKeywords)
        {
            if (lower.Contains(banned)) return false;
        }
        return true;
    }

    private static string? StripSternSuffix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        foreach (var suffix in SternTitleSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length].Trim();
                break;
            }
        }
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string SlugToTitle(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return slug;
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var ti = CultureInfo.InvariantCulture.TextInfo;
        return string.Join(' ', parts.Select(p => ti.ToTitleCase(p)));
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
