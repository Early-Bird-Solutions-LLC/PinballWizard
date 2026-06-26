using PinballWizard.Application.Sync;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

public static class LinkingUtilities
{
    // Maps a scraped document's SourceType to the canonical manufacturer
    // partition key (matching Machine.PartitionKey). Each manufacturer-specific
    // scraper only ever discovers that manufacturer's own documents, so the
    // SourceType is an authoritative manufacturer signal — used to disambiguate
    // title-slug collisions across manufacturers (e.g. "Godzilla" exists for
    // both Sega 1998 and Stern 2021; a ManualsPage/GamePage document is Stern's).
    // Keys are sourced from ScraperManufacturerKey — never hand-typed — so they
    // match the partition keys the OPDB sync wrote (e.g. CGC is "cgc",
    // American Pinball is "americanpinball"). Returns null for any future
    // SourceType not yet mapped, so the linker falls back to its existing
    // (un-disambiguated) behavior rather than mis-resolving.
    public static string? InferManufacturerKey(SourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.SourceType switch
        {
            // Stern's three scrapers (the original, unprefixed manufacturer).
            SourceType.ManualsPage => ScraperManufacturerKey.Stern,
            SourceType.GamePage => ScraperManufacturerKey.Stern,
            SourceType.ServiceBulletinPage => ScraperManufacturerKey.Stern,
            SourceType.JjpProductPage => ScraperManufacturerKey.Jjp,
            SourceType.JjpSupportPage => ScraperManufacturerKey.Jjp,
            SourceType.AmericanPinballGamePage => ScraperManufacturerKey.AmericanPinball,
            SourceType.SpookyPinballGamePage => ScraperManufacturerKey.Spooky,
            SourceType.SpookyPinballSupportPage => ScraperManufacturerKey.Spooky,
            SourceType.PinballBrothersGamePage => ScraperManufacturerKey.PinballBrothers,
            SourceType.PinballBrothersDocumentPage => ScraperManufacturerKey.PinballBrothers,
            SourceType.BarrelsOfFunProductPage => ScraperManufacturerKey.BarrelsOfFun,
            SourceType.ChicagoGamingGamePage => ScraperManufacturerKey.ChicagoGaming,
            SourceType.MultimorphicProductPage => ScraperManufacturerKey.Multimorphic,
            _ => null,
        };
    }

    // Edition markers in priority order (longer strings first to avoid
    // "le" winning before "limited" when both appear).
    private static readonly (string Marker, string Canonical)[] EditionMarkers =
    [
        ("premium", "Premium"),
        ("limited", "Limited"),
        ("pro", "Pro"),
        ("le", "LE"),
        ("vault", "Vault"),
        ("ce", "CE"),
    ];

    public static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var lower = value.ToLowerInvariant();
        // Replace runs of separators and whitespace with single spaces
        var sb = new System.Text.StringBuilder(lower.Length);
        var lastWasSeparator = false;
        foreach (var c in lower)
        {
            var isSeparator = c == '_' || c == '-' || c == '.' || char.IsWhiteSpace(c);
            if (isSeparator)
            {
                if (!lastWasSeparator)
                {
                    sb.Append(' ');
                }
                lastWasSeparator = true;
            }
            else
            {
                sb.Append(c);
                lastWasSeparator = false;
            }
        }
        return sb.ToString().Trim();
    }

    public static bool IsWordBoundaryMatch(string normText, string normSlug)
    {
        if (string.IsNullOrEmpty(normSlug) || string.IsNullOrEmpty(normText))
            return false;
        var paddedText = " " + normText + " ";
        var paddedSlug = " " + normSlug + " ";
        return paddedText.Contains(paddedSlug, StringComparison.Ordinal);
    }

    public static string? ExtractEditionFromText(string normalizedText)
    {
        foreach (var (marker, canonical) in EditionMarkers)
        {
            if (normalizedText.Contains(marker, StringComparison.Ordinal))
                return canonical;
        }
        return null;
    }

    public static string? ExtractEdition(string normFilename, string normSlug)
    {
        var idx = normFilename.IndexOf(normSlug, StringComparison.Ordinal);
        if (idx < 0) return null;

        var afterSlug = idx + normSlug.Length;
        if (afterSlug >= normFilename.Length) return null;

        var tail = normFilename[afterSlug..];

        // Skip leading space if present
        if (tail.StartsWith(' '))
        {
            tail = tail[1..];
        }

        if (tail.Length == 0) return null;

        foreach (var (marker, canonical) in EditionMarkers)
        {
            if (tail.StartsWith(marker, StringComparison.Ordinal))
                return canonical;
        }
        return null;
    }

    public static string? ExtractGameSlugFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("game", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }
        return null;
    }
}
