using System.Globalization;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;

namespace PinballWizard.Infrastructure.Scraping.JsonLd;

/// <summary>
/// Shared parser for <c>schema.org/Product</c> JSON-LD blocks
/// embedded in storefront product pages. Handles every shape we've
/// seen across JJP (Shopify), Barrels of Fun (WooCommerce, nested
/// price-specification array), and Multimorphic (WooCommerce, both
/// flat and nested price simultaneously, <c>http://schema.org</c>
/// availability URLs).
/// </summary>
/// <remarks>
/// Extracted to a shared helper after three storefronts shipped
/// near-identical copies of this code (the threshold called out in
/// PR #38's review and PR #39's CHANGELOG note). Each manufacturer
/// extractor keeps its own DOM-fallback logic (og:title / h1 / slug
/// prettification) and its <c>GameRecord</c>-construction logic;
/// what they share is the JSON-LD walk.
/// <para>
/// Pure functions — no I/O. The entry point is
/// <see cref="FindFirstProduct"/>; nothing else needs to be public,
/// but <see cref="ReadProduct"/> is exposed for tests that want to
/// exercise the per-element type-matching independently.
/// </para>
/// </remarks>
public static class JsonLdProductParser
{
    /// <summary>
    /// Walks every <c>&lt;script type='application/ld+json'&gt;</c>
    /// block in <paramref name="doc"/> and returns the first
    /// <c>schema.org/Product</c> entry, supporting all three known
    /// container shapes:
    /// <list type="bullet">
    ///   <item>Bare <c>{ "@type": "Product", ... }</c> object (Shopify).</item>
    ///   <item>Array <c>[{ "@type": "WebSite" }, { "@type": "Product" }]</c> (some Shopify themes).</item>
    ///   <item><c>@graph</c> wrapper <c>{ "@graph": [{ "@type": "Product" }] }</c> (Yoast / RankMath SEO plugins on WordPress / WooCommerce).</item>
    /// </list>
    /// Malformed JSON-LD blocks are skipped silently — a broken block
    /// must not block extraction from a sibling block.
    /// </summary>
    public static JsonLdProduct? FindFirstProduct(IHtmlDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

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

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("@graph", out var graph)
                && graph.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in graph.EnumerateArray())
                {
                    if (ReadProduct(item) is { } prod) return prod;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (ReadProduct(item) is { } prod) return prod;
                }
            }
            else
            {
                if (ReadProduct(root) is { } prod) return prod;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns a <see cref="JsonLdProduct"/> if
    /// <paramref name="element"/> is a JSON object whose
    /// <c>@type</c> is — or includes — <c>Product</c>. Returns null
    /// otherwise. Exposed publicly so tests can exercise the
    /// type-matching surface against an isolated <see cref="JsonElement"/>.
    /// </summary>
    public static JsonLdProduct? ReadProduct(JsonElement element)
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

    private static JsonLdOffer? ReadOffers(JsonElement element)
    {
        if (!element.TryGetProperty("offers", out var offers)) return null;

        var first = offers.ValueKind switch
        {
            JsonValueKind.Object => offers,
            JsonValueKind.Array => offers.EnumerateArray().FirstOrDefault(o => o.ValueKind == JsonValueKind.Object),
            _ => default,
        };
        if (first.ValueKind != JsonValueKind.Object) return null;

        return new JsonLdOffer
        {
            Price = ReadPriceFromOffer(first),
            Availability = ReadString(first, "availability"),
        };
    }

    private static string? ReadPriceFromOffer(JsonElement offer)
    {
        // Flat shape (Shopify, Multimorphic): offers[].price
        if (offer.TryGetProperty("price", out var direct) && FormatPrice(direct) is { } flat)
        {
            return flat;
        }

        // Nested shape (WooCommerce): offers[].priceSpecification.price.
        // The priceSpecification value is sometimes an object (Multimorphic)
        // and sometimes an array (Barrels of Fun); both are handled.
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

    /// <summary>
    /// Emits the invariant-#17 visibility signals when no JSON-LD product is found
    /// on a storefront product page: a <see cref="LogLevel.Warning"/> log and an
    /// increment of <c>pinwiz.scraper.jsonld_missing_total</c> tagged with the
    /// source name and the product URL. Call this after <see cref="FindFirstProduct"/>
    /// returns null when a logger is available. The record is still returned via the
    /// OG / H1 fallback — this method provides the observable signal for the
    /// degradation, not a change in behavior.
    /// </summary>
    public static void WarnJsonLdMissing(ILogger logger, Uri productUrl, string source)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(productUrl);
        ArgumentNullException.ThrowIfNull(source);

        logger.LogWarning(
            "No JSON-LD product found on {Url} for {Source}; falling back to OpenGraph/h1 — structured fields (editions/price/status) will be absent.",
            productUrl,
            source);

        PinballWizardTelemetry.ScraperJsonLdMissing.Add(
            1,
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("url", productUrl.ToString()));
    }
}
