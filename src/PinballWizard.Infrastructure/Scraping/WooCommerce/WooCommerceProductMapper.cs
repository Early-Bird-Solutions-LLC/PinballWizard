using System.Globalization;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.WooCommerce;

internal static class WooCommerceProductMapper
{
    private static readonly HtmlParser HtmlParser = new();

    internal static GameRecord? MapToGameRecord(
        WooCommerceStoreProductDto product,
        string gameIdPrefix,
        string discoveredOnTag)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameIdPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(discoveredOnTag);

        var slug = ExtractSlug(product.Permalink);
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var title = product.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var status = product.IsInStock ? "in_stock" : "out_of_stock";
        var msrp = TryParsePrice(product.Prices);
        var description = StripHtml(product.ShortDescription ?? product.Description);
        var images = product.Images
            .Select(i => i.Src)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();

        var editions = msrp is not null
            ? new List<EditionInfo>
            {
                new()
                {
                    Name = "Standard",
                    Msrp = msrp,
                    Availability = status,
                    Description = description,
                    ImageUrls = images,
                }
            }
            : new List<EditionInfo>();

        return new GameRecord
        {
            GameId = $"{gameIdPrefix}{slug}",
            Title = title,
            Slug = slug,
            GamePageUrl = product.Permalink,
            DiscoveredOn = [discoveredOnTag],
            Status = status,
            Editions = editions,
            Source = new GameSourceInfo
            {
                ScrapedFrom = product.Permalink,
                ScrapedAt = DateTime.UtcNow,
            },
        };
    }

    private static string? ExtractSlug(string? permalink)
    {
        if (string.IsNullOrWhiteSpace(permalink))
            return null;

        var segments = permalink.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(segments[i]))
                return segments[i];
        }

        return null;
    }

    private static string? TryParsePrice(WooCommerceStorePricesDto? prices)
    {
        if (prices?.Price is null or { Length: 0 })
            return null;

        if (!decimal.TryParse(prices.Price, NumberStyles.Number, CultureInfo.InvariantCulture, out var raw))
            return null;

        if (raw <= 0)
            return null;

        var divisor = (decimal)Math.Pow(10, prices.CurrencyMinorUnit);
        var value = raw / divisor;
        return value.ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        using var doc = HtmlParser.ParseDocument(html);
        return doc.Body?.TextContent?.Trim();
    }
}
