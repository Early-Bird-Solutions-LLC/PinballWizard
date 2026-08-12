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
            // ServiceBulletinPage was originally Stern-only. AP bulletin documents
            // scraped before #827 carry this type because issue #762 (re-scrape does
            // not update scraper-owned fields) means the stored source_type is never
            // corrected. Distinguish by host URL — the AP CDN is american-pinball.com
            // for both the support page and its s4.* file-serving subdomain.
            // New AP bulletins use the dedicated ApBulletinPage value below.
            SourceType.ServiceBulletinPage when IsAmericanPinballUrl(source.FileUrl)
                => ScraperManufacturerKey.AmericanPinball,
            SourceType.ServiceBulletinPage => ScraperManufacturerKey.Stern,
            // Dedicated AP bulletin type introduced in #827. Scrapes after #827 emit
            // this value, so they never need the URL-based fallback above.
            SourceType.ApBulletinPage => ScraperManufacturerKey.AmericanPinball,
            SourceType.JjpProductPage => ScraperManufacturerKey.Jjp,
            SourceType.JjpSupportPage => ScraperManufacturerKey.Jjp,
            SourceType.AmericanPinballGamePage => ScraperManufacturerKey.AmericanPinball,
            SourceType.SpookyPinballGamePage => ScraperManufacturerKey.Spooky,
            SourceType.SpookyPinballSupportPage => ScraperManufacturerKey.Spooky,
            SourceType.PinballBrothersGamePage => ScraperManufacturerKey.PinballBrothers,
            SourceType.PinballBrothersDocumentPage => ScraperManufacturerKey.PinballBrothers,
            SourceType.PinballBrothersFreshdeskArticle => ScraperManufacturerKey.PinballBrothers,
            SourceType.BarrelsOfFunProductPage => ScraperManufacturerKey.BarrelsOfFun,
            SourceType.ChicagoGamingGamePage => ScraperManufacturerKey.ChicagoGaming,
            SourceType.MultimorphicProductPage => ScraperManufacturerKey.Multimorphic,
            // Synthesized articles (Kineticist / Tilt Forums / TWIP) are cross-manufacturer
            // knowledge sources persisted as PlatformGeneric documents — they never enter the
            // linker's title-collision disambiguation, and their manufacturer travels per-record
            // on DocumentRecord.Manufacturer rather than being inferable from the source type.
            // So there is deliberately no single manufacturer key here.
            SourceType.SynthesizedArticle => null,
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

        // Insert boundary spaces at camelCase / acronym / letter-digit transitions
        // so a concatenated filename title ("JamesBond007") tokenizes like a
        // separator-delimited slug ("james-bond-007"). Without this, slug
        // "james bond 007" can never word-boundary-match the single token
        // "jamesbond007" (corpus-mislink bug 1a).
        var lower = InsertTokenBoundaries(value).ToLowerInvariant();
        // Replace runs of separators and whitespace with single spaces
        var sb = new System.Text.StringBuilder(lower.Length);
        var lastWasSeparator = false;
        foreach (var c in lower)
        {
            // Apostrophes are stripped entirely so "Elvira's" matches "elviras" and
            // "Batman '66" matches "batman 66".
            if (c == '\'') continue;

            // ':' and '/' and '()' and '!' are separators so titles like "AC/DC" or
            // "Batman: The Dark Knight" or "The Avengers (Pro)" normalize like their
            // hyphenated/spaced slugs.
            var isSeparator = c == '_' || c == '-' || c == '.' || c == '&' || c == ':' || c == '/' || c == '(' || c == ')' || c == '!' || char.IsWhiteSpace(c);
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

    // Inserts a space at camelCase / acronym→word / letter↔digit transitions so
    // a concatenated title tokenizes like a separator-delimited slug. All-caps
    // runs with no trailing lowercase ("TMNT") are left intact; "TMNTGame" splits
    // to "TMNT Game". Operates on the case-bearing input (before lowercasing).
    private static string InsertTokenBoundaries(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && IsTokenBoundary(value, i))
            {
                sb.Append(' ');
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    private static bool IsTokenBoundary(string value, int i)
    {
        var prev = value[i - 1];
        var c = value[i];
        if (char.IsLower(prev) && char.IsUpper(c)) return true;   // james|Bond
        if (char.IsLetter(prev) && char.IsDigit(c)) return true;  // Bond|007
        if (char.IsDigit(prev) && char.IsLetter(c)) return true;  // 007|Special
        // Acronym→word: an uppercase run followed by upper-then-lower (TMNT|Game).
        return char.IsUpper(prev) && char.IsUpper(c)
            && i + 1 < value.Length && char.IsLower(value[i + 1]);
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

    // Matches the American Pinball registrable domain. AP bulletin files are served
    // from s4.american-pinball.com (CDN subdomain); the support page itself is
    // www.american-pinball.com. Both match this predicate.
    //
    // Parses the HOST rather than substring-matching the whole URL: manufacturer
    // attribution decides which machines a document can bind to, so a query string
    // or path segment that merely mentions the domain (".../redirect?to=american-pinball.com")
    // must not silently re-attribute another manufacturer's document.
    private const string ApDomain = "american-pinball.com";

    private static bool IsAmericanPinballUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return host.Equals(ApDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + ApDomain, StringComparison.OrdinalIgnoreCase);
    }
}
