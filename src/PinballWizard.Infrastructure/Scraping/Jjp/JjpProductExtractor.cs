using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.JsonLd;
using PinballWizard.Infrastructure.Scraping.OpenGraph;

namespace PinballWizard.Infrastructure.Scraping.Jjp;

/// <summary>
/// Extracts a <see cref="GameRecord"/> from a JJP product page's
/// rendered HTML. Pure functions — no I/O. Follows the
/// machine-consumer-metadata-first principle: prefer JSON-LD product
/// schema and Open Graph tags over rendered-DOM scraping.
/// </summary>
/// <remarks>
/// Shopify's product pages embed JSON-LD with
/// <c>"@type": "Product"</c> containing name, description, image,
/// offers (with price + availability), and brand. That's our primary
/// source. og:title / og:image / og:description are the secondary
/// fallbacks. DOM scraping is the last resort.
/// <para>
/// JSON-LD parsing is delegated to
/// <see cref="JsonLdProductParser"/> — the shared helper that also
/// powers BoF and Multimorphic.
/// </para>
/// </remarks>
public static class JjpProductExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Extracts a <see cref="GameRecord"/> from the supplied product
    /// page HTML. Returns null if the page does not look like a
    /// pinball machine product (no name, no JSON-LD product, etc.).
    /// </summary>
    /// <param name="html">Rendered HTML of a JJP product page.</param>
    /// <param name="productUrl">Absolute URL of the product page.</param>
    public static GameRecord? Extract(string html, Uri productUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(productUrl);

        using var doc = Parser.ParseDocument(html);

        var slug = ExtractSlug(productUrl);
        if (string.IsNullOrWhiteSpace(slug)) return null;

        var product = JsonLdProductParser.FindFirstProduct(doc);
        var title = product?.Name
            ?? OpenGraphExtractor.GetMetaContent(doc, "og:title")
            ?? doc.QuerySelector("h1")?.TextContent?.Trim();

        if (string.IsNullOrWhiteSpace(title)) return null;

        var description = product?.Description ?? OpenGraphExtractor.GetMetaContent(doc, "og:description");
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
            GameId = $"game_jjp_{slug}",
            Title = title.Trim(),
            Slug = slug,
            GamePageUrl = productUrl.ToString(),
            DiscoveredOn = ["jjp_products"],
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
    /// Extracts the slug segment from a JJP product URL like
    /// <c>https://jerseyjackpinball.com/products/dialed-in</c> →
    /// <c>"dialed-in"</c>.
    /// </summary>
    public static string? ExtractSlug(Uri productUrl)
    {
        ArgumentNullException.ThrowIfNull(productUrl);
        var segments = productUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("products", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }
        return null;
    }

    private static List<string> CollectImageUrls(JsonLdProduct? product, IHtmlDocument doc)
    {
        var images = product?.Images.ToList() ?? [];

        var ogImage = OpenGraphExtractor.GetMetaContent(doc, "og:image");
        if (!string.IsNullOrWhiteSpace(ogImage) && !images.Contains(ogImage, StringComparer.OrdinalIgnoreCase))
        {
            images.Add(ogImage);
        }
        return images;
    }

    /// <summary>
    /// Normalises a Schema.org availability URL or token to a
    /// short-form string (<c>in_stock</c>, <c>out_of_stock</c>,
    /// <c>preorder</c>, <c>discontinued</c>). Returns null on blank
    /// input. Mirrors <c>BofProductExtractor.NormalizeAvailability</c>
    /// and <c>MultimorphicProductExtractor.NormalizeAvailability</c>.
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
}
