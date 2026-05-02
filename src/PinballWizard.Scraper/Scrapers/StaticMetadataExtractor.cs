using System.Globalization;
using System.Text.Json;
using System.Web;
using AngleSharp.Dom;
using PinballWizard.Scraper.Models;

namespace PinballWizard.Scraper.Scrapers;

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
    /// One-shot convenience: extract every machine-readable field from a
    /// parsed Stern game page in a single pass.
    /// </summary>
    public static StaticGameMetadata Extract(IDocument document)
    {
        return new StaticGameMetadata
        {
            Title = ExtractTitle(document),
            DatePublished = ExtractDatePublished(document),
            CanonicalUrl = ExtractCanonicalUrl(document),
            Editions = ExtractEditionsFromContactLinks(document),
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
    /// Walks every <c>&lt;a&gt;</c> on the page, keeps the ones pointing at
    /// <c>shop.sternpinball.com/pages/contact-for-availability</c> with a
    /// <c>variant</c> query param, and decodes <c>name</c> / <c>msrp</c> /
    /// <c>availability</c> from their query strings.
    /// </summary>
    /// <remarks>
    /// The first generic link on each page (no <c>variant</c>, no <c>price</c>)
    /// is the game-wide "contact us" form and is filtered out — only the
    /// per-edition links carry structured data.
    /// </remarks>
    public static List<EditionInfo> ExtractEditionsFromContactLinks(IDocument document)
    {
        var editions = new List<EditionInfo>();

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;
            if (!href.Contains(ContactAvailabilityHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (!href.Contains(ContactAvailabilityPathFragment, StringComparison.OrdinalIgnoreCase)) continue;

            var edition = ParseEditionFromUrl(href);
            if (edition is not null) editions.Add(edition);
        }

        // The same edition can appear more than once if the page renders the
        // contact button in multiple cards. Reuse the existing dedupe so we
        // get one EditionInfo per unique name with non-null fields merged.
        return GamePageExtractors.DeduplicateEditions(editions);
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
}
