using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Ap;

/// <summary>
/// Reads American Pinball's sitemap.xml (a flat urlset, not an
/// index) and returns the subset of URLs that are game pages.
/// Sitemap-first discovery is preferred over DOM scraping per the
/// locked feedback memory <c>feedback_machine_consumer_metadata_first.md</c>.
/// </summary>
/// <remarks>
/// AP's sitemap is much simpler than JJP's Shopify one — single
/// urlset containing every page (home / games / news / support).
/// We filter by the <see cref="ApOptions.GamePathPrefix"/> path and
/// further reject URLs that look like sub-pages of a game (e.g.,
/// <c>/games/{slug}/updates</c>) so the per-game scrape only hits
/// the canonical game page once.
/// </remarks>
public sealed class ApSitemapClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly ApOptions _options;

    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>Initializes a new <see cref="ApSitemapClient"/>.</summary>
    public ApSitemapClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<ApOptions> apOptions,
        ILogger<ApSitemapClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(apOptions);
        _httpClient = httpClient;
        _options = apOptions.Value;
    }

    /// <summary>
    /// Returns the full set of game-page URLs discovered in the
    /// sitemap.
    /// </summary>
    public async Task<List<Uri>> DiscoverGameUrlsAsync(CancellationToken cancellationToken)
    {
        var sitemapUrl = new Uri(new Uri(_options.BaseUrl), _options.SitemapPath);
        Logger.LogInformation("AP: reading sitemap at {Url}", sitemapUrl);

        var body = await GetStringPolitelyAsync(_httpClient, sitemapUrl, cancellationToken).ConfigureAwait(false);
        var urls = ParseGameUrls(body, _options.GamePathPrefix);

        Logger.LogInformation("AP: discovered {Count} game-page URLs from sitemap", urls.Count);
        return urls;
    }

    /// <summary>
    /// Parses an AP sitemap XML body and returns the URLs whose
    /// absolute path begins with <paramref name="gamePathPrefix"/>
    /// AND has exactly one slug segment after the prefix (rejects
    /// sub-pages like <c>/games/{slug}/updates</c>).
    /// </summary>
    public static List<Uri> ParseGameUrls(string sitemapXml, string gamePathPrefix)
    {
        ArgumentNullException.ThrowIfNull(sitemapXml);
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePathPrefix);

        var doc = XDocument.Parse(sitemapXml);
        var urls = new List<Uri>();

        var normalizedPrefix = gamePathPrefix.EndsWith('/') ? gamePathPrefix : gamePathPrefix + "/";

        foreach (var url in doc.Descendants(SitemapNs + "url"))
        {
            var loc = url.Element(SitemapNs + "loc")?.Value;
            if (string.IsNullOrWhiteSpace(loc)) continue;
            if (!Uri.TryCreate(loc, UriKind.Absolute, out var uri)) continue;

            var path = uri.AbsolutePath;
            if (!path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            // Reject sub-pages: only accept paths with one slug segment after the prefix.
            var afterPrefix = path[normalizedPrefix.Length..].TrimEnd('/');
            if (afterPrefix.Length == 0) continue; // /games/ itself
            if (afterPrefix.Contains('/', StringComparison.Ordinal)) continue; // /games/{slug}/updates etc.

            urls.Add(uri);
        }

        return urls;
    }
}
