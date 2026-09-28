using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.ChicagoGaming;

/// <summary>
/// Reads the page at <see cref="ChicagoGamingOptions.MachinesIndexPath"/>
/// (the site root) and returns the set of canonical machine URLs
/// linked from its site-wide "Pinball" navigation menu.
/// </summary>
/// <remarks>
/// CGC removed the dedicated <c>/coinop/</c> index in August 2026
/// (#967). The Pinball dropdown in the shared site header is now the
/// only complete listing: <c>/sitemap.xml</c> is a 2019 generator
/// snapshot that omits Cactus Canyon and Pulp Fiction, robots.txt does
/// not advertise it, and no page carries JSON-LD.
/// <para>
/// The header also links <c>/coinop/cactus-canyon/upgrade</c>, and
/// machine pages expose <c>/coinop/{slug}/update</c> and
/// <c>/coinop/{slug}/update/mac</c>. Those are sub-pages of a machine,
/// so the parser requires exactly one slug segment after the
/// configured prefix.
/// </para>
/// </remarks>
public sealed class CgcMenuClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly ChicagoGamingOptions _options;

    private static readonly HtmlParser Parser = new();

    /// <summary>Initializes a new <see cref="CgcMenuClient"/>.</summary>
    public CgcMenuClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<ChicagoGamingOptions> cgcOptions,
        ILogger<CgcMenuClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(cgcOptions);
        _httpClient = httpClient;
        _options = cgcOptions.Value;
    }

    /// <summary>
    /// Fetches the configured machines index page and returns the
    /// deduplicated set of canonical machine URLs. A successful fetch
    /// that yields none is an error: an empty set is not a completed
    /// scrape.
    /// </summary>
    public async Task<List<Uri>> DiscoverMachineUrlsAsync(CancellationToken cancellationToken)
    {
        var indexUrl = new Uri(new Uri(_options.BaseUrl), _options.MachinesIndexPath);
        Logger.LogInformation("Chicago Gaming: reading machines index {Url}", indexUrl);

        var html = await GetStringPolitelyAsync(_httpClient, indexUrl, cancellationToken).ConfigureAwait(false);
        var urls = ParseMachineLinks(html, _options.BaseUrl, _options.GamePathPrefix);

        if (urls.Count == 0)
        {
            Logger.LogError(
                "Chicago Gaming: machines index {Url} was fetched but linked no {Prefix}{{slug}} machine pages. The site navigation has likely changed.",
                indexUrl, _options.GamePathPrefix);
            throw new InvalidOperationException(
                $"Chicago Gaming machines index at {indexUrl} linked 0 machine pages under {_options.GamePathPrefix}.");
        }

        Logger.LogInformation(
            "Chicago Gaming: machines index yielded {Count} canonical machine URL(s)", urls.Count);
        return urls;
    }

    /// <summary>
    /// Parses an index page's HTML and returns the deduplicated set
    /// of canonical machine URLs whose absolute path begins with
    /// <paramref name="gamePathPrefix"/> AND has exactly one slug
    /// segment after the prefix. Hosts are restricted to match
    /// <paramref name="baseUrl"/> so external links cannot pollute
    /// the result.
    /// </summary>
    public static List<Uri> ParseMachineLinks(string html, string baseUrl, string gamePathPrefix)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePathPrefix);

        var baseUri = new Uri(baseUrl);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new List<Uri>();

        var normalizedPrefix = gamePathPrefix.EndsWith('/') ? gamePathPrefix : gamePathPrefix + "/";

        using var doc = Parser.ParseDocument(html);
        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseUri, href, out var absolute)) continue;

            if (!string.Equals(absolute.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (!absolute.AbsolutePath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            // Single-slug-segment requirement rejects /coinop/ itself, /coinop/{slug}/update,
            // /coinop/{slug}/update/mac, /coinop/cactus-canyon/upgrade, etc.
            var afterPrefix = absolute.AbsolutePath[normalizedPrefix.Length..].TrimEnd('/');
            if (afterPrefix.Length == 0) continue;
            if (afterPrefix.Contains('/', StringComparison.Ordinal)) continue;

            // Drop fragment / query so anchor variants of the same machine
            // canonicalise to one URL in the result set.
            var canonical = new UriBuilder(absolute) { Fragment = "", Query = "" }.Uri;
            if (seen.Add(canonical.AbsoluteUri))
            {
                urls.Add(canonical);
            }
        }

        return urls;
    }
}
