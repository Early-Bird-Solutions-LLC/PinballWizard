using System.Globalization;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.BarrelsOfFun;

/// <summary>
/// Extracts a <see cref="GameRecord"/> from a Barrels of Fun /
/// kollectfun.com WooCommerce product page. Pure functions — no I/O.
/// Same machine-consumer-metadata-first pattern as the JJP extractor:
/// JSON-LD <c>schema.org/Product</c> first, Open Graph as fallback,
/// DOM as last resort.
/// </summary>
/// <remarks>
/// WooCommerce's JSON-LD nests price under
/// <c>offers[].priceSpecification[].price</c> (per
/// <c>UnitPriceSpecification</c>); Shopify (the JJP shape) puts price
/// directly on <c>offers[].price</c>. This extractor reads both —
/// nested wins if present, flat is the fallback — so the same code
/// would work against another WooCommerce-on-WordPress storefront.
/// </remarks>
public static class BofProductExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Extracts a <see cref="GameRecord"/> from a product page.
    /// Returns null if the page is missing the required signals
    /// (no slug, no title, or no JSON-LD product / og:title fallback).
    /// </summary>
    public static GameRecord? Extract(string html, Uri productUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(productUrl);

        using var doc = Parser.ParseDocument(html);

        var slug = ExtractSlug(productUrl);
        if (string.IsNullOrWhiteSpace(slug)) return null;

        var product = FindFirstProductJsonLd(doc);
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
            GameId = $"game_barrelsoffun_{slug}",
            Title = title.Trim(),
            Slug = slug,
            GamePageUrl = productUrl.ToString(),
            DiscoveredOn = ["barrelsoffun_machines_category"],
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
    /// Pulls the slug from a URL like
    /// <c>https://shop.kollectfun.com/product/jim-hensons-labyrinth/</c>
    /// → <c>"jim-hensons-labyrinth"</c>.
    /// </summary>
    public static string? ExtractSlug(Uri productUrl)
    {
        ArgumentNullException.ThrowIfNull(productUrl);
        var segments = productUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("product", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }
        return null;
    }

    /// <summary>
    /// Normalises a Schema.org availability URL or token to a
    /// short-form string (<c>in_stock</c>, <c>out_of_stock</c>,
    /// <c>preorder</c>, <c>discontinued</c>). Returns null on blank
    /// input. Mirrors <c>JjpProductExtractor.NormalizeAvailability</c>.
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

            // WooCommerce sometimes wraps JSON-LD in @graph; sometimes not.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in graph.EnumerateArray())
                {
                    if (TryReadProduct(item) is { } prod) return prod;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
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
            JsonValueKind.Array => typeProp.EnumerateArray().Any(t =>
                t.ValueKind == JsonValueKind.String
                && string.Equals(t.GetString(), "Product", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
        if (!typeMatch) return null;

        return new JsonLdProduct
        {
            Name = ReadString(element, "name"),
            Description = ReadString(element, "description"),
            Images = ReadImages(element),
            Offers = ReadOffers(element),
        };
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
            Price = ReadPriceFromOffer(first),
            Availability = ReadString(first, "availability"),
        };
    }

    private static string? ReadPriceFromOffer(JsonElement offer)
    {
        // Flat shape (Shopify): offers[].price
        if (offer.TryGetProperty("price", out var direct))
        {
            if (FormatPrice(direct) is { } flat) return flat;
        }

        // Nested shape (WooCommerce): offers[].priceSpecification[].price
        if (offer.TryGetProperty("priceSpecification", out var spec))
        {
            var pickedSpec = spec.ValueKind switch
            {
                JsonValueKind.Object => spec,
                JsonValueKind.Array => spec.EnumerateArray().FirstOrDefault(s => s.ValueKind == JsonValueKind.Object),
                _ => default,
            };
            if (pickedSpec.ValueKind == JsonValueKind.Object
                && pickedSpec.TryGetProperty("price", out var nestedPrice))
            {
                return FormatPrice(nestedPrice);
            }
        }

        return null;
    }

    private static string? FormatPrice(JsonElement price) => price.ValueKind switch
    {
        JsonValueKind.String => price.GetString(),
        JsonValueKind.Number => price.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
