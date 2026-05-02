using System.Globalization;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

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

        var jsonLdProduct = FindFirstProductJsonLd(doc);
        var title = jsonLdProduct?.Name
            ?? GetMetaContent(doc, "og:title")
            ?? doc.QuerySelector("h1")?.TextContent?.Trim();

        if (string.IsNullOrWhiteSpace(title)) return null;

        var description = jsonLdProduct?.Description
            ?? GetMetaContent(doc, "og:description");

        var images = CollectImageUrls(jsonLdProduct, doc);

        var availability = jsonLdProduct?.Offers?.Availability;
        var status = NormalizeAvailability(availability);

        var price = jsonLdProduct?.Offers?.Price;

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

    private static JsonLdProduct? FindFirstProductJsonLd(IHtmlDocument doc)
    {
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            var text = script.TextContent;
            if (string.IsNullOrWhiteSpace(text)) continue;

            JsonElement root;
            try
            {
                using var parsed = JsonDocument.Parse(text);
                root = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            // Some Shopify themes wrap JSON-LD in an array; some don't.
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (TryReadProduct(item) is { } prod) return prod;
                }
            }
            else
            {
                if (TryReadProduct(root) is { } prod) return prod;
            }
        }
        return null;
    }

    private static JsonLdProduct? TryReadProduct(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (!element.TryGetProperty("@type", out var typeProp)) return null;

        var typeMatch = typeProp.ValueKind switch
        {
            JsonValueKind.String => string.Equals(typeProp.GetString(), "Product", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Array => typeProp.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && string.Equals(t.GetString(), "Product", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
        if (!typeMatch) return null;

        var product = new JsonLdProduct
        {
            Name = element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
            Description = element.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString() : null,
            Images = ReadImages(element),
            Offers = ReadOffers(element),
        };
        return product;
    }

    private static List<string> ReadImages(JsonElement element)
    {
        var images = new List<string>();
        if (!element.TryGetProperty("image", out var imageProp)) return images;

        switch (imageProp.ValueKind)
        {
            case JsonValueKind.String:
                if (imageProp.GetString() is { Length: > 0 } single) images.Add(single);
                break;
            case JsonValueKind.Array:
                foreach (var item in imageProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    {
                        images.Add(s);
                    }
                }
                break;
            default:
                break;
        }
        return images;
    }

    private static JsonLdOffers? ReadOffers(JsonElement element)
    {
        if (!element.TryGetProperty("offers", out var offers)) return null;

        var first = offers.ValueKind switch
        {
            JsonValueKind.Object => offers,
            JsonValueKind.Array => offers.EnumerateArray().FirstOrDefault(o => o.ValueKind == JsonValueKind.Object),
            _ => default,
        };
        if (first.ValueKind != JsonValueKind.Object) return null;

        return new JsonLdOffers
        {
            Price = first.TryGetProperty("price", out var price) ? FormatPrice(price) : null,
            Availability = first.TryGetProperty("availability", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : null,
        };
    }

    private static string? FormatPrice(JsonElement price) => price.ValueKind switch
    {
        JsonValueKind.String => price.GetString(),
        JsonValueKind.Number => price.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

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

    private static string? NormalizeAvailability(string? schemaOrgAvailability)
    {
        if (string.IsNullOrWhiteSpace(schemaOrgAvailability)) return null;

        var trimmed = schemaOrgAvailability;
        var lastSegment = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return lastSegment?.ToLowerInvariant() switch
        {
            "instock" => "in_stock",
            "outofstock" => "out_of_stock",
            "preorder" => "preorder",
            "discontinued" => "discontinued",
            _ => lastSegment?.ToLowerInvariant(),
        };
    }

    private sealed class JsonLdProduct
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
        public List<string> Images { get; init; } = [];
        public JsonLdOffers? Offers { get; init; }
    }

    private sealed class JsonLdOffers
    {
        public string? Price { get; init; }
        public string? Availability { get; init; }
    }
}
