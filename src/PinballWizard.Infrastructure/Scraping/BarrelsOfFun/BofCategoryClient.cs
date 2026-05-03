using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.BarrelsOfFun;

/// <summary>
/// Reads the Barrels of Fun storefront's pinball-machines category
/// page and returns the set of <c>/product/{slug}/</c> URLs that the
/// per-product scraper should visit. The category page is the
/// canonical filter — same defence-in-depth pattern as JJP's
/// collection-handle filter (<c>JjpSitemapClient.FilterByHandleSet</c>),
/// keeping merch / apparel / parts out of the machine catalog.
/// </summary>
/// <remarks>
/// The storefront is WooCommerce on WordPress — the category page is
/// server-rendered HTML containing anchors to each in-category
/// product. There is no machine-consumer JSON endpoint that returns
/// the category's product set without auth, so the canonical filter
/// is "anchors on this page that point at <c>/product/{slug}/</c>".
/// </remarks>
public sealed class BofCategoryClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly BarrelsOfFunOptions _options;

    private static readonly HtmlParser Parser = new();

    /// <summary>Initializes a new <see cref="BofCategoryClient"/>.</summary>
    public BofCategoryClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<BarrelsOfFunOptions> bofOptions,
        ILogger<BofCategoryClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(bofOptions);
        _httpClient = httpClient;
        _options = bofOptions.Value;
    }

    /// <summary>
    /// Returns the set of canonical product URLs found on the
    /// configured machines category page.
    /// </summary>
    public async Task<List<Uri>> DiscoverMachineUrlsAsync(CancellationToken cancellationToken)
    {
        var categoryUrl = new Uri(new Uri(_options.BaseUrl), _options.MachinesCategoryPath);
        Logger.LogInformation("Barrels of Fun: reading machines category {Url}", categoryUrl);

        var html = await GetStringPolitelyAsync(_httpClient, categoryUrl, cancellationToken).ConfigureAwait(false);
        var urls = ParseProductLinks(html, _options.BaseUrl, _options.ProductPathPrefix);

        Logger.LogInformation(
            "Barrels of Fun: machines category yielded {Count} machine product URL(s)", urls.Count);
        return urls;
    }

    /// <summary>
    /// Parses a category page's HTML and returns the deduplicated set
    /// of canonical product URLs whose absolute path begins with
    /// <paramref name="productPathPrefix"/>. Hosts are restricted to
    /// match <paramref name="baseUrl"/> so external links cannot
    /// pollute the result.
    /// </summary>
    public static List<Uri> ParseProductLinks(string html, string baseUrl, string productPathPrefix)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(productPathPrefix);

        var baseUri = new Uri(baseUrl);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new List<Uri>();

        using var doc = Parser.ParseDocument(html);
        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseUri, href, out var absolute)) continue;

            if (!string.Equals(absolute.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (!absolute.AbsolutePath.StartsWith(productPathPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            // Reject links to sub-pages of a product (e.g. /product/x/reviews) by
            // requiring exactly one path segment after the prefix.
            var afterPrefix = absolute.AbsolutePath[productPathPrefix.Length..].TrimEnd('/');
            if (afterPrefix.Length == 0) continue;
            if (afterPrefix.Contains('/', StringComparison.Ordinal)) continue;

            // Drop the fragment and query so two anchor variants of the same
            // product canonicalise to one URL in the result set.
            var canonical = new UriBuilder(absolute) { Fragment = "", Query = "" }.Uri;
            if (seen.Add(canonical.AbsoluteUri))
            {
                urls.Add(canonical);
            }
        }

        return urls;
    }
}
