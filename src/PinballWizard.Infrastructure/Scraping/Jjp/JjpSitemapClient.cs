using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// JJP's sitemap index and the product sitemaps it references,
    /// filtered to the handle set of the configured pinball-machine
    /// collection. Apparel / banners / accessories that share the
    /// <c>/products/</c> URL space are excluded.
    /// </summary>
    public async Task<List<Uri>> DiscoverProductUrlsAsync(CancellationToken cancellationToken)
    {
        var machineHandles = await FetchPinballMachineHandlesAsync(cancellationToken).ConfigureAwait(false);
        Logger.LogInformation(
            "JJP: collection '{Collection}' contains {Count} pinball-machine handle(s)",
            _options.PinballMachinesCollectionSlug, machineHandles.Count);

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
            var filtered = FilterByHandleSet(urls, machineHandles);
            products.AddRange(filtered);
            Logger.LogInformation(
                "JJP: sitemap {Sitemap} yielded {Total} product URL(s); {Kept} matched the machine collection",
                sitemapUrl, urls.Count, filtered.Count);
        }

        Logger.LogInformation("JJP: discovered {Count} machine product URL(s) from sitemap", products.Count);
        return products;
    }

    /// <summary>
    /// Fetches <c>/collections/{slug}/products.json</c> and returns
    /// the set of product handles in that collection. Shopify exposes
    /// this endpoint as machine-consumer JSON — the canonical source
    /// of "what's actually a pinball machine" on JJP's storefront.
    /// </summary>
    public async Task<HashSet<string>> FetchPinballMachineHandlesAsync(CancellationToken cancellationToken)
    {
        var path = $"/collections/{_options.PinballMachinesCollectionSlug}/products.json?limit=250";
        var url = new Uri(new Uri(_options.BaseUrl), path);
        var body = await GetStringPolitelyAsync(_httpClient, url, cancellationToken).ConfigureAwait(false);
        return ParseHandlesFromCollectionJson(body);
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

    /// <summary>
    /// Parses a Shopify collection <c>products.json</c> body and
    /// returns the set of product handles. Returns an empty set
    /// rather than throwing on malformed JSON.
    /// </summary>
    public static HashSet<string> ParseHandlesFromCollectionJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return handles;

        try
        {
            var payload = JsonSerializer.Deserialize<ShopifyCollectionResponse>(json);
            if (payload?.Products is null) return handles;

            foreach (var product in payload.Products)
            {
                if (!string.IsNullOrWhiteSpace(product.Handle))
                {
                    handles.Add(product.Handle);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed body — empty set lets the caller decide whether
            // to treat the collection as missing (no machines) or to
            // surface the error via outer logging.
        }

        return handles;
    }

    /// <summary>
    /// Returns the subset of <paramref name="urls"/> whose final path
    /// segment (after <c>/products/</c>) is in
    /// <paramref name="handles"/>.
    /// </summary>
    public static List<Uri> FilterByHandleSet(IEnumerable<Uri> urls, HashSet<string> handles)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(handles);

        var result = new List<Uri>();
        foreach (var uri in urls)
        {
            var handle = ExtractProductHandle(uri);
            if (handle is not null && handles.Contains(handle))
            {
                result.Add(uri);
            }
        }
        return result;
    }

    private static string? ExtractProductHandle(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("products", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }
        return null;
    }

    private sealed class ShopifyCollectionResponse
    {
        [JsonPropertyName("products")]
        public List<ShopifyProductLite>? Products { get; init; }
    }

    private sealed class ShopifyProductLite
    {
        [JsonPropertyName("handle")]
        public string? Handle { get; init; }
    }
}
