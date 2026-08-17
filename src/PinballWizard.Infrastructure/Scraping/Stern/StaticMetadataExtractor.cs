using System.Globalization;
using System.Text.Json;
using System.Web;
using AngleSharp.Dom;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Stern;

/// <summary>
/// Reads what sternpinball.com publishes in static HTML for machine consumers —
/// Open Graph tags, Schema.org JSON-LD, canonical link, and the
/// <c>contact-for-availability</c> shop links that carry edition + MSRP +
/// availability in their query strings. No Playwright, no Vue render needed.
/// </summary>
/// <remarks>
/// Reading site-published metadata is preferred over scraping rendered DOM —
/// it's both more reliable (stable across CSS refactors) and more polite
/// (uses the channel the site explicitly publishes for external tools).
/// See <c>docs/metadata-audit.md</c> Tier 2.
/// </remarks>
public static class StaticMetadataExtractor
{
    /// <summary>
    /// Stern's <c>contact-for-availability</c> shop URL prefix. Per-edition
    /// links live at this host with <c>variant</c>, <c>price</c>, and <c>title</c>
    /// query parameters.
    /// </summary>
    private const string ContactAvailabilityHost = "shop.sternpinball.com";

    private const string ContactAvailabilityPathFragment = "/pages/contact-for-availability";

    /// <summary>
    /// Stern's replacement checkout path, introduced ~2026-08. Relative links on
    /// the game page now use <c>/contact-to-buy?ip-family=…&amp;product-name=…&amp;variant={code}</c>.
    /// The edition display name is encoded in the <c>data-track-id</c> attribute
    /// (slug form, e.g. <c>limited-edition</c>); price is no longer in the URL.
    /// </summary>
    private const string ContactToBuyPathFragment = "/contact-to-buy";

    /// <summary>
    /// One-shot convenience: extract every machine-readable field from a
    /// parsed Stern game page in a single pass.
    /// </summary>
    /// <param name="document">The parsed game page.</param>
    /// <param name="slug">
    /// The game's slug (e.g. <c>"aerosmith"</c>), used only as the fallback
    /// path prefix — see <see cref="ExtractEditionsFromSubpageLinks"/>.
    /// </param>
    public static StaticGameMetadata Extract(IDocument document, string slug)
    {
        var editions = ExtractEditionsFromContactLinks(document);
        var usedFallback = editions.Count == 0;
        if (usedFallback)
        {
            // Some games (aerosmith, batman-66, beatles, ...) publish only the
            // generic page-wide contact-to-buy link — no per-edition variant=
            // links at all — but still expose per-edition sub-pages in the
            // game's own nav. Fall back to those rather than record zero
            // editions for a game that plainly has more than one.
            editions = ExtractEditionsFromSubpageLinks(document, slug);
        }

        return new StaticGameMetadata
        {
            Title = ExtractTitle(document),
            DatePublished = ExtractDatePublished(document),
            CanonicalUrl = ExtractCanonicalUrl(document),
            Editions = editions,
            EditionsFromNavFallback = usedFallback,
        };
    }

    /// <summary>
    /// Pulls the canonical title from <c>&lt;meta property="og:title"&gt;</c>,
    /// stripping the trailing " - Stern Pinball" / " | Stern Pinball" suffix.
    /// Reuses <see cref="GamePageExtractors.SanitizeGameTitle"/> for suffix
    /// handling so we can't drift away from the rules used in the DOM path.
    /// </summary>
    public static string? ExtractTitle(IDocument document)
    {
        var ogTitle = ReadMeta(document, "property", "og:title");
        var twitterTitle = ReadMeta(document, "name", "twitter:title");

        // The hidden contact form input is the cleanest source — Stern populates
        // it server-side with the canonical game title (no Stern-Pinball suffix).
        var formTitle = document
            .QuerySelector("input#contact-form-product-title")?
            .GetAttribute("value")?
            .Trim();

        var candidates = new[] { formTitle, ogTitle, twitterTitle };
        var sanitized = GamePageExtractors.SanitizeGameTitle(
            candidates: candidates,
            pageTitle: document.Title,
            slug: string.Empty); // empty slug → SanitizeGameTitle won't synthesize a fallback

        return string.IsNullOrEmpty(sanitized) ? null : sanitized;
    }

    /// <summary>
    /// Reads the JSON-LD <c>datePublished</c> from the AIOSEO-emitted
    /// schema graph. Stern emits this on every game page; it's roughly when
    /// the page went live. Returns null on any parse failure — never throws.
    /// </summary>
    public static DateTime? ExtractDatePublished(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            var json = script.TextContent;
            if (string.IsNullOrWhiteSpace(json)) continue;

            var parsed = TryParseDatePublished(json);
            if (parsed is not null) return parsed;
        }

        return null;
    }

    /// <summary>
    /// Reads <c>&lt;link rel="canonical"&gt;</c>. Useful for verifying the
    /// scraped URL matches what the site considers the authoritative URL.
    /// </summary>
    public static string? ExtractCanonicalUrl(IDocument document)
    {
        var href = document
            .QuerySelector("link[rel='canonical']")?
            .GetAttribute("href")?
            .Trim();
        return string.IsNullOrEmpty(href) ? null : href;
    }

    /// <summary>
    /// Walks every <c>&lt;a&gt;</c> on the page and extracts per-edition data
    /// from contact-purchase links. Handles both the legacy
    /// <c>shop.sternpinball.com/pages/contact-for-availability</c> pattern
    /// (which carries edition name + MSRP in query params) and the replacement
    /// <c>/contact-to-buy</c> pattern introduced ~2026-08 (relative URL, edition
    /// name in <c>data-track-id</c>, MSRP absent from URL).
    /// </summary>
    /// <remarks>
    /// The generic page-wide "Where To Buy" link (no <c>variant</c> param) is
    /// filtered out — only per-edition links carry structured data.
    /// </remarks>
    public static List<EditionInfo> ExtractEditionsFromContactLinks(IDocument document)
    {
        var editions = new List<EditionInfo>();

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            EditionInfo? edition;
            if (href.Contains(ContactAvailabilityHost, StringComparison.OrdinalIgnoreCase)
                && href.Contains(ContactAvailabilityPathFragment, StringComparison.OrdinalIgnoreCase))
            {
                // Legacy pattern: absolute URL to shop.sternpinball.com with
                // variant + price query params.
                edition = ParseEditionFromUrl(href);
            }
            else if (href.Contains(ContactToBuyPathFragment, StringComparison.OrdinalIgnoreCase))
            {
                // New pattern (~2026-08): relative /contact-to-buy link with
                // variant code in query string and display name in data-track-id.
                edition = ParseEditionFromContactToBuyAnchor(anchor);
            }
            else
            {
                continue;
            }

            if (edition is not null) editions.Add(edition);
        }

        // The same edition can appear more than once if the page renders the
        // contact button in multiple cards. Reuse the existing dedupe so we
        // get one EditionInfo per unique name with non-null fields merged.
        return GamePageExtractors.DeduplicateEditions(editions);
    }

    /// <summary>
    /// The literal string a broken client-side template emits when an edition
    /// slot's data failed to populate. Confirmed on the live
    /// <c>sternpinball.com/game/beatles/</c> page (2026-08-17): a genuine
    /// <c>&lt;a href="/game/beatles/undefined"&gt;</c>, styled identically to
    /// the real Diamond/Gold edition links — there is no DOM signal that
    /// distinguishes it, only this exact value.
    /// </summary>
    private const string UndefinedEditionArtifact = "undefined";

    /// <summary>
    /// Fallback for games whose page carries no per-edition contact link at
    /// all (only the generic page-wide "Where To Buy" button) but that still
    /// link to per-edition sub-pages in their own nav, of the form
    /// <c>/game/{slug}/{edition}</c> — e.g. <c>/game/aerosmith/pro</c>.
    /// Only called when <see cref="ExtractEditionsFromContactLinks"/> returns
    /// zero editions, so a game whose contact links already work is never
    /// double-counted against its own nav.
    /// </summary>
    /// <remarks>
    /// Matching tolerance mirrors <see cref="ExtractEditionsFromContactLinks"/>:
    /// both a root-relative href (<c>/game/{slug}/pro</c>) and an absolute one
    /// on the same host, and both with and without a trailing slash, resolve
    /// to the same path. Verified live against 7 real Stern game pages
    /// (2026-08-17): edition names are NOT a fixed vocabulary — aerosmith uses
    /// pro/premium/limited-edition, beatles uses Diamond/Gold — so this
    /// deliberately does not filter by an edition-name allow-list, only by
    /// path shape plus the one confirmed non-edition artifact above.
    /// </remarks>
    public static List<EditionInfo> ExtractEditionsFromSubpageLinks(IDocument document, string slug)
    {
        var prefix = $"/game/{slug}/";
        var editions = new List<EditionInfo>();

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            var path = ExtractPath(href);
            if (path is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            // Path decoding happens BEFORE the structural checks below, so an
            // encoded '/', '?', or '#' cannot smuggle a nested path or query
            // past the single-segment check by hiding inside a percent-escape.
            var editionSlug = Uri.UnescapeDataString(path[prefix.Length..].TrimEnd('/'));

            // Reject anything but a single path segment — a nested path (a
            // linked PDF under a documents/ sub-path), a query string, or a
            // fragment (an in-page anchor like #overview) is not an edition
            // slug. Also reject the one confirmed non-edition value.
            if (editionSlug.Length == 0
                || editionSlug.Contains('/', StringComparison.Ordinal)
                || editionSlug.Contains('?', StringComparison.Ordinal)
                || editionSlug.Contains('#', StringComparison.Ordinal)
                || string.Equals(editionSlug, UndefinedEditionArtifact, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            editions.Add(new EditionInfo { Name = TitleCaseSlug(editionSlug) });
        }

        return GamePageExtractors.DeduplicateEditions(editions);
    }

    /// <summary>
    /// Resolves an anchor href — root-relative or absolute-on-sternpinball.com
    /// — down to its path component, so the caller can compare against a
    /// <c>/game/{slug}/...</c> prefix regardless of which form the page used.
    /// Returns null for anything else (a different host, a mailto: link, ...).
    /// </summary>
    private static string? ExtractPath(string href)
    {
        if (href.StartsWith('/')) return href;

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        return uri.Host.Equals("sternpinball.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".sternpinball.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath
            : null;
    }

    /// <summary>
    /// Parses a single <c>contact-for-availability</c> URL into an
    /// <see cref="EditionInfo"/>. Returns null when no <c>variant</c> param
    /// is present — that's the page-wide generic contact link, not a per-edition one.
    /// </summary>
    public static EditionInfo? ParseEditionFromUrl(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;

        var query = HttpUtility.ParseQueryString(uri.Query);
        var variant = query["variant"];
        if (string.IsNullOrWhiteSpace(variant)) return null;

        var rawPrice = query["price"];
        var priceTrimmed = string.IsNullOrWhiteSpace(rawPrice) ? null : rawPrice.Trim();

        // "SOLD OUT" sits in the price slot — it's an availability signal,
        // not a number. Promote it to Availability and leave Msrp null.
        string? msrp = null;
        string? availability = null;
        if (priceTrimmed is not null)
        {
            if (priceTrimmed.Contains("sold out", StringComparison.OrdinalIgnoreCase))
            {
                availability = "sold_out";
            }
            else
            {
                msrp = priceTrimmed;
            }
        }

        return new EditionInfo
        {
            Name = variant.Trim(),
            Msrp = msrp,
            Availability = availability,
        };
    }

    /// <summary>
    /// Parses a single new-style <c>/contact-to-buy</c> anchor into an
    /// <see cref="EditionInfo"/>. Returns null when the link has no <c>variant</c>
    /// query param — those are the page-wide generic "Where To Buy" buttons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stern replaced the absolute <c>shop.sternpinball.com/pages/contact-for-availability</c>
    /// links with relative <c>/contact-to-buy?ip-family=…&amp;product-name=…&amp;variant={code}</c>
    /// links (~2026-08). The <c>variant</c> param uses short codes (<c>LE</c>, <c>AE</c>)
    /// for multi-word editions; the <c>data-track-id</c> attribute holds the slug form
    /// (<c>limited-edition</c>, <c>anniversary-edition</c>) from which a readable
    /// display name is derived. Price is not present in the new URL; MSRP is left null.
    /// </para>
    /// <para>
    /// The <c>variant</c> query param is used as a fallback display name when
    /// <c>data-track-id</c> is absent or does not match the expected format.
    /// </para>
    /// </remarks>
    public static EditionInfo? ParseEditionFromContactToBuyAnchor(IElement anchor)
    {
        var href = anchor.GetAttribute("href");
        if (string.IsNullOrEmpty(href)) return null;
        if (!href.Contains(ContactToBuyPathFragment, StringComparison.OrdinalIgnoreCase)) return null;

        // Relative URLs need a dummy base to parse with Uri/HttpUtility.
        var absoluteHref = href.StartsWith('/')
            ? "https://sternpinball.com" + href
            : href;

        if (!Uri.TryCreate(absoluteHref, UriKind.Absolute, out var uri)) return null;

        var query = HttpUtility.ParseQueryString(uri.Query);
        var variant = query["variant"];
        if (string.IsNullOrWhiteSpace(variant)) return null;

        // Prefer the data-track-id slug for a human-readable display name;
        // fall back to title-casing the variant code directly.
        var trackId = anchor.GetAttribute("data-track-id");
        var name = (!string.IsNullOrEmpty(trackId) ? ParseDisplayNameFromTrackId(trackId) : null)
                   ?? TitleCaseSlug(variant.Trim());

        return new EditionInfo
        {
            Name = name,
            Msrp = null, // price is no longer encoded in the new URL
        };
    }

    /// <summary>
    /// Parses the slug from a <c>data-track-id</c> of the form
    /// <c>"Buy Now button for: {slug}; in Game Card on the Game Page: {game}"</c>
    /// and converts it to a title-cased display name.
    /// Returns null when the format doesn't match.
    /// </summary>
    private static string? ParseDisplayNameFromTrackId(string trackId)
    {
        // Find the first ": " separator — the slug follows it.
        var colonIdx = trackId.IndexOf(": ", StringComparison.Ordinal);
        if (colonIdx < 0) return null;

        var start = colonIdx + 2;
        var semiIdx = trackId.IndexOf(';', start);
        var slug = semiIdx >= 0
            ? trackId[start..semiIdx].Trim()
            : trackId[start..].Trim();

        return string.IsNullOrEmpty(slug) ? null : TitleCaseSlug(slug);
    }

    /// <summary>
    /// Title-cases a hyphen-delimited slug into a display name.
    /// <c>"limited-edition"</c> → <c>"Limited Edition"</c>,
    /// <c>"pro"</c> → <c>"Pro"</c>.
    /// </summary>
    private static string TitleCaseSlug(string slug)
    {
        var words = slug.Split('-');
        return string.Join(
            ' ',
            words.Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static string? ReadMeta(IDocument document, string keyAttribute, string keyValue)
    {
        var content = document
            .QuerySelector($"meta[{keyAttribute}='{keyValue}']")?
            .GetAttribute("content")?
            .Trim();
        return string.IsNullOrEmpty(content) ? null : content;
    }

    private static DateTime? TryParseDatePublished(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindDatePublished(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime? FindDatePublished(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("datePublished", out var datePublished)
                    && datePublished.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(
                        datePublished.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    return parsed;
                }
                foreach (var prop in element.EnumerateObject())
                {
                    var nested = FindDatePublished(prop.Value);
                    if (nested is not null) return nested;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindDatePublished(item);
                    if (nested is not null) return nested;
                }
                break;
        }

        return null;
    }
}

/// <summary>
/// Bundle of fields extracted from a single static HTML fetch of a Stern
/// game page. Any field can be null if the page didn't publish it.
/// </summary>
public sealed class StaticGameMetadata
{
    public string? Title { get; init; }
    public DateTime? DatePublished { get; init; }
    public string? CanonicalUrl { get; init; }
    public List<EditionInfo> Editions { get; init; } = [];

    /// <summary>
    /// True when <see cref="Editions"/> came from
    /// <see cref="StaticMetadataExtractor.ExtractEditionsFromSubpageLinks"/>
    /// because the primary contact-link strategy found none — regardless of
    /// whether the fallback itself recovered any editions. A degraded path
    /// having executed is itself worth surfacing (OBS-04): if Stern changes
    /// the contact-link pattern site-wide, every game silently loses Msrp/
    /// Availability (only the name-only fallback carries a name) while the
    /// run still looks healthy, unless this is visible somewhere.
    /// </summary>
    public bool EditionsFromNavFallback { get; init; }
}
