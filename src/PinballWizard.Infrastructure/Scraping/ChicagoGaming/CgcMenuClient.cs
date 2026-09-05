using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.ChicagoGaming;

/// <summary>
/// Reads the configured CGC index page (see
/// <c>ChicagoGamingOptions.MachinesIndexPath</c> — the site root since CGC
/// retired <c>/coinop/</c>, #967) and returns the set of canonical machine
/// URLs. The site's sitemap is incomplete in practice (omits some shipped
/// machines); that page is the canonical source — same defence-in-depth as
/// <c>BofCategoryClient</c>.
/// </summary>
/// <remarks>
/// CGC pages also expose <c>/coinop/{slug}/update</c> (firmware /
/// release notes) and <c>/coinop/{slug}/update/mac</c> (Mac-specific
/// updates). Those are sub-pages of a machine, not separate
/// machines, so the parser requires exactly one slug segment after
/// the configured prefix.
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
    /// deduplicated set of canonical machine URLs.
    /// </summary>
    public async Task<List<Uri>> DiscoverMachineUrlsAsync(CancellationToken cancellationToken)
    {
        var indexUrl = new Uri(new Uri(_options.BaseUrl), _options.MachinesIndexPath);
        Logger.LogInformation("Chicago Gaming: reading machines index {Url}", indexUrl);

        var html = await GetStringPolitelyAsync(_httpClient, indexUrl, cancellationToken).ConfigureAwait(false);
        var urls = ParseMachineLinks(html, _options.BaseUrl, _options.GamePathPrefix);

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
            // /coinop/{slug}/update/mac, etc.
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
