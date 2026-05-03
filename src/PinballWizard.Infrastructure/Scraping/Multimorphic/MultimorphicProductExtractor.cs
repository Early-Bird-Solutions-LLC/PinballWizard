using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.JsonLd;

namespace PinballWizard.Infrastructure.Scraping.Multimorphic;

/// <summary>
/// Extracts a <see cref="GameRecord"/> from a Multimorphic product
/// page. Pure functions — no I/O. Same machine-consumer-metadata-first
/// pattern as the JJP and BoF extractors: JSON-LD
/// <c>schema.org/Product</c> first, Open Graph as fallback, DOM as
/// last resort.
/// </summary>
/// <remarks>
/// JSON-LD parsing is delegated to <see cref="JsonLdProductParser"/>
/// — the shared helper that handles both flat
/// <c>offers[].price</c> and nested
/// <c>offers[].priceSpecification</c> shapes (object or array) plus
/// <c>@graph</c> wrapping. Multimorphic ships both flat and nested
/// price entries simultaneously and uses <c>http://schema.org/...</c>
/// (not <c>https://</c>) for availability URLs; the shared parser
/// preserves the flat-shape preference and
/// <see cref="NormalizeAvailability"/> here is protocol-agnostic
/// (it reads only the trailing path segment).
/// </remarks>
public static class MultimorphicProductExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Extracts a <see cref="GameRecord"/> from a product page.
    /// Returns null if the page is missing the required signals
    /// (no slug, no title).
    /// </summary>
    public static GameRecord? Extract(string html, Uri productUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(productUrl);

        using var doc = Parser.ParseDocument(html);

        var slug = ExtractSlug(productUrl);
        if (string.IsNullOrWhiteSpace(slug)) return null;

        var product = JsonLdProductParser.FindFirstProduct(doc);
        var title = product?.Name
            ?? GetMetaContent(doc, "og:title")
            ?? doc.QuerySelector("h1")?.TextContent?.Trim();

        if (string.IsNullOrWhiteSpace(title)) return null;

        var description = product?.Description ?? GetMetaContent(doc, "og:description");
        var images = CollectImageUrls(product, doc);
        var availability = product?.Offers?.Availability;
        var status = NormalizeAvailability(availability);
        var price = product?.Offers?.Price;

        var editions = price is not null
            ? new List<EditionInfo>
            {
                new()
                {
                    Name = "Standard",
                    Msrp = price,
                    Availability = status,
                    Description = description,
                    ImageUrls = images,
                }
            }
            : new List<EditionInfo>();

        return new GameRecord
        {
            GameId = $"game_multimorphic_{slug}",
            Title = title.Trim(),
            Slug = slug,
            GamePageUrl = productUrl.ToString(),
            DiscoveredOn = ["multimorphic_game_kits"],
            Status = status,
            Editions = editions,
            Source = new GameSourceInfo
            {
                ScrapedFrom = productUrl.ToString(),
                ScrapedAt = DateTime.UtcNow,
            },
        };
    }

    /// <summary>
    /// Extracts the trailing slug segment from a Multimorphic game
    /// kit URL such as
    /// <c>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/cannon-lagoon/</c>
    /// → <c>"cannon-lagoon"</c>.
    /// </summary>
    public static string? ExtractSlug(Uri productUrl)
    {
        ArgumentNullException.ThrowIfNull(productUrl);
        var segments = productUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("multimorphic-game-kits", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }
        return null;
    }

    /// <summary>
    /// Normalises a Schema.org availability URL or token to a
    /// short-form string (<c>in_stock</c>, <c>out_of_stock</c>,
    /// <c>preorder</c>, <c>discontinued</c>). Multimorphic uses
    /// <c>http://schema.org/...</c> (not <c>https://</c>); the trailing
    /// segment is what matters and the protocol is irrelevant.
    /// </summary>
    public static string? NormalizeAvailability(string? schemaOrgAvailability)
    {
        if (string.IsNullOrWhiteSpace(schemaOrgAvailability)) return null;

        var lastSegment = schemaOrgAvailability.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return lastSegment?.ToLowerInvariant() switch
        {
            "instock" => "in_stock",
            "outofstock" => "out_of_stock",
            "preorder" => "preorder",
            "discontinued" => "discontinued",
            _ => lastSegment?.ToLowerInvariant(),
        };
    }

    private static string? GetMetaContent(IHtmlDocument doc, string property)
    {
        var meta = doc.QuerySelector($"meta[property='{property}']")
            ?? doc.QuerySelector($"meta[name='{property}']");
        return meta?.GetAttribute("content")?.Trim();
    }

    private static List<string> CollectImageUrls(JsonLdProduct? product, IHtmlDocument doc)
    {
        var images = product?.Images.ToList() ?? [];
        var ogImage = GetMetaContent(doc, "og:image");
        if (!string.IsNullOrWhiteSpace(ogImage) && !images.Contains(ogImage, StringComparer.OrdinalIgnoreCase))
        {
            images.Add(ogImage);
        }
        return images;
    }
}
