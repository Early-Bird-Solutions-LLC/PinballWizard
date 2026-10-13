using System.Linq;

namespace PinballWizard.Application.Rag.GameOverviews;

// Cookie-consent and privacy-banner paragraphs that Stern (and the same
// class of CMP widgets) inject into the rendered game page. They are not
// game prose. A paragraph is dropped when it carries one of these markers;
// a page whose only long paragraphs are banners yields no overview.
public static class OverviewProseFilter
{
    private static readonly string[] BannerMarkers =
    [
        "cookie",
        "consent",
        "your privacy",
        "privacy choices",
        "manage preferences",
        "do not sell",
    ];

    public static bool IsConsentBanner(string? paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph)) return false;
        foreach (var marker in BannerMarkers)
        {
            if (paragraph.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Splits on the blank line the extractor uses between paragraphs.
    // Returns null when every paragraph is banner text.
    public static string? WithoutConsentBanner(string? prose)
    {
        if (string.IsNullOrWhiteSpace(prose)) return null;

        var kept = prose.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !IsConsentBanner(part))
            .ToList();

        return kept.Count == 0 ? null : string.Join("\n\n", kept);
    }
}
