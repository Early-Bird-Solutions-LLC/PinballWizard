using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Multimorphic;

/// <summary>
/// Reads Multimorphic's WordPress sitemap index, walks the product
/// sub-sitemaps, and returns the subset of product URLs whose path
/// begins with <see cref="MultimorphicOptions.MultimorphicGameKitsPathPrefix"/>.
/// Sitemap-first discovery per
/// <c>feedback_machine_consumer_metadata_first.md</c>.
/// </summary>
/// <remarks>
/// Site shape: WordPress + WooCommerce + Yoast-style sitemap. The
/// top-level <c>/wp-sitemap.xml</c> is an index referencing
/// <c>wp-sitemap-posts-product-N.xml</c> sub-sitemaps. We follow
/// only those sub-sitemaps; non-product sitemaps (pages, posts,
/// taxonomies) are ignored at the index layer.
/// </remarks>
public sealed class MultimorphicSitemapClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly MultimorphicOptions _options;

    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>Initializes a new <see cref="MultimorphicSitemapClient"/>.</summary>
    public MultimorphicSitemapClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<MultimorphicOptions> multimorphicOptions,
        ILogger<MultimorphicSitemapClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(multimorphicOptions);
        _httpClient = httpClient;
        _options = multimorphicOptions.Value;
    }

    /// <summary>
    /// Walks the sitemap index → product sub-sitemaps and returns
    /// every URL whose absolute path begins with the configured
    /// game-kits prefix.
    /// </summary>
    public async Task<List<Uri>> DiscoverGameKitUrlsAsync(CancellationToken cancellationToken)
    {
        var indexUrl = new Uri(new Uri(_options.BaseUrl), _options.SitemapPath);
        Logger.LogInformation("Multimorphic: reading sitemap index at {Url}", indexUrl);

        var indexBody = await GetStringPolitelyAsync(_httpClient, indexUrl, cancellationToken).ConfigureAwait(false);
        var productSitemaps = ParseProductSitemapsFromIndex(indexBody);

        Logger.LogInformation(
            "Multimorphic: index references {Count} product sub-sitemap(s)", productSitemaps.Count);

        var urls = new List<Uri>();
        foreach (var sitemapUrl in productSitemaps)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var body = await GetStringPolitelyAsync(_httpClient, sitemapUrl, cancellationToken).ConfigureAwait(false);
            var filtered = ParseGameKitUrls(body, _options.MultimorphicGameKitsPathPrefix);
            urls.AddRange(filtered);
            Logger.LogInformation(
                "Multimorphic: sub-sitemap {Sitemap} yielded {Count} game-kit URL(s)",
                sitemapUrl, filtered.Count);
        }

        Logger.LogInformation(
            "Multimorphic: discovered {Count} Multimorphic-published game kit URL(s)", urls.Count);
        return urls;
    }

    /// <summary>
    /// Parses the sitemap index XML and returns the URLs of any
    /// child sitemap whose <c>loc</c> path includes
    /// <c>wp-sitemap-posts-product</c>.
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
            if (loc.Contains("wp-sitemap-posts-product", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(loc, UriKind.Absolute, out var uri))
            {
                sitemaps.Add(uri);
            }
        }

        return sitemaps;
    }

    /// <summary>
    /// Parses a product sub-sitemap and returns URLs whose absolute
    /// path begins with <paramref name="pathPrefix"/> AND has exactly
    /// one slug segment after the prefix (rejects further-nested
    /// sub-pages such as <c>/store/p3-game-kits/multimorphic-game-kits/{slug}/something</c>).
    /// </summary>
    public static List<Uri> ParseGameKitUrls(string sitemapXml, string pathPrefix)
    {
        ArgumentNullException.ThrowIfNull(sitemapXml);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);

        var doc = XDocument.Parse(sitemapXml);
        var urls = new List<Uri>();

        var normalizedPrefix = pathPrefix.EndsWith('/') ? pathPrefix : pathPrefix + "/";

        foreach (var url in doc.Descendants(SitemapNs + "url"))
        {
            var loc = url.Element(SitemapNs + "loc")?.Value;
            if (string.IsNullOrWhiteSpace(loc)) continue;
            if (!Uri.TryCreate(loc, UriKind.Absolute, out var uri)) continue;

            var path = uri.AbsolutePath;
            if (!path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var afterPrefix = path[normalizedPrefix.Length..].TrimEnd('/');
            if (afterPrefix.Length == 0) continue;
            if (afterPrefix.Contains('/', StringComparison.Ordinal)) continue;

            urls.Add(uri);
        }

        return urls;
    }
}
