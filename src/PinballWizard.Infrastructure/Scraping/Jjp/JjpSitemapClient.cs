using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Jjp;

/// <summary>
/// Reads JJP's Shopify-generated sitemap index, walks each product
/// sitemap, and returns the set of <c>/products/{slug}</c> URLs.
/// Sitemap-first discovery is preferred over DOM scraping per the
/// locked feedback memory <c>feedback_machine_consumer_metadata_first.md</c>:
/// the sitemap is the canonical, machine-consumer-intended view of
/// what JJP wants discoverable.
/// </summary>
/// <remarks>
/// Shopify's sitemap.xml is an INDEX containing references to per-type
/// sitemaps (<c>sitemap_products_*.xml</c>, <c>sitemap_pages_*.xml</c>,
/// <c>sitemap_collections_*.xml</c>, etc.). We only follow the
/// product sitemaps. The product sitemaps in turn list the absolute
/// canonical URL for every product page on the storefront.
/// </remarks>
public sealed class JjpSitemapClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly JjpOptions _options;

    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>Initializes a new <see cref="JjpSitemapClient"/>.</summary>
    public JjpSitemapClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<JjpOptions> jjpOptions,
        ILogger<JjpSitemapClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(jjpOptions);
        _httpClient = httpClient;
        _options = jjpOptions.Value;
    }

    /// <summary>
    /// Returns every <c>/products/{slug}</c> URL discovered by walking
    /// JJP's sitemap index and the product sitemaps it references.
    /// </summary>
    public async Task<List<Uri>> DiscoverProductUrlsAsync(CancellationToken cancellationToken)
    {
        var indexUrl = new Uri(new Uri(_options.BaseUrl), _options.SitemapPath);
        Logger.LogInformation("JJP: reading sitemap index at {Url}", indexUrl);

        var indexBody = await GetStringPolitelyAsync(_httpClient, indexUrl, cancellationToken).ConfigureAwait(false);
        var productSitemaps = ParseProductSitemapsFromIndex(indexBody);

        Logger.LogInformation("JJP: index references {Count} product sitemap(s)", productSitemaps.Count);

        var products = new List<Uri>();
        foreach (var sitemapUrl in productSitemaps)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var body = await GetStringPolitelyAsync(_httpClient, sitemapUrl, cancellationToken).ConfigureAwait(false);
            var urls = ParseProductUrls(body);
            products.AddRange(urls);
            Logger.LogInformation("JJP: sitemap {Sitemap} yielded {Count} product URLs", sitemapUrl, urls.Count);
        }

        Logger.LogInformation("JJP: discovered {Count} total product URLs from sitemap", products.Count);
        return products;
    }

    /// <summary>
    /// Parses the sitemap index XML and returns the URLs of any
    /// child sitemap whose loc path includes <c>sitemap_products</c>.
    /// </summary>
    public static List<Uri> ParseProductSitemapsFromIndex(string indexXml)
    {
        ArgumentNullException.ThrowIfNull(indexXml);

        var doc = XDocument.Parse(indexXml);
        var sitemaps = new List<Uri>();

        foreach (var sitemap in doc.Descendants(SitemapNs + "sitemap"))
        {
            var loc = sitemap.Element(SitemapNs + "loc")?.Value;
            if (string.IsNullOrWhiteSpace(loc)) continue;
            if (loc.Contains("sitemap_products", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(loc, UriKind.Absolute, out var uri))
            {
                sitemaps.Add(uri);
            }
        }

        return sitemaps;
    }

    /// <summary>
    /// Parses a product sitemap XML and returns the URLs of every
    /// <c>/products/{slug}</c> page listed in it. Other paths are
    /// ignored.
    /// </summary>
    public static List<Uri> ParseProductUrls(string sitemapXml)
    {
        ArgumentNullException.ThrowIfNull(sitemapXml);

        var doc = XDocument.Parse(sitemapXml);
        var urls = new List<Uri>();

        foreach (var url in doc.Descendants(SitemapNs + "url"))
        {
            var loc = url.Element(SitemapNs + "loc")?.Value;
            if (string.IsNullOrWhiteSpace(loc)) continue;
            if (!Uri.TryCreate(loc, UriKind.Absolute, out var uri)) continue;
            if (!uri.AbsolutePath.Contains("/products/", StringComparison.OrdinalIgnoreCase)) continue;
            urls.Add(uri);
        }

        return urls;
    }
}
