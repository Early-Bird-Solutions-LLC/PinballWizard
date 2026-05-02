using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// Reads Pinball Brothers' WordPress REST API to enumerate pages and
/// returns the subset whose slug identifies them as game pages. Uses
/// the machine-consumer endpoint per the locked feedback memory
/// <c>feedback_machine_consumer_metadata_first.md</c>.
/// </summary>
/// <remarks>
/// Game-page filter: a WP page is a Pinball Brothers game page iff
/// its slug ends with the configured <c>GameSlugSuffix</c> (default
/// <c>-pinball</c>). Today this matches <c>queen-pinball</c>,
/// <c>alien-pinball</c>, <c>abba-pinball</c>, <c>predator-pinball</c>;
/// the convention has held across each title shipped on the
/// post-2023 site so the suffix is the cheapest reliable signal.
/// </remarks>
public sealed class PbWpPagesClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly PinballBrothersOptions _options;

    /// <summary>Initializes a new <see cref="PbWpPagesClient"/>.</summary>
    public PbWpPagesClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<PinballBrothersOptions> pbOptions,
        ILogger<PbWpPagesClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(pbOptions);
        _httpClient = httpClient;
        _options = pbOptions.Value;
    }

    /// <summary>
    /// Enumerates Pinball Brothers' WP pages (paginated) and returns
    /// the subset that pass the slug-suffix game filter.
    /// </summary>
    public async Task<List<PbPageRaw>> DiscoverGamePagesAsync(CancellationToken cancellationToken)
    {
        var allPages = new List<PbPageRaw>();
        int page = 1;
        while (true)
        {
            var url = BuildPagesUrl(page);
            Logger.LogInformation("Pinball Brothers: reading WP pages {Url}", url);

            var body = await GetStringPolitelyAsync(_httpClient, url, cancellationToken).ConfigureAwait(false);
            var batch = ParsePagesJson(body);
            if (batch.Count == 0) break;

            allPages.AddRange(batch);

            if (batch.Count < _options.PageSize) break;

            page++;
            if (page > _options.MaxPagesToFetch)
            {
                Logger.LogWarning(
                    "Pinball Brothers: MaxPagesToFetch ({Cap}) reached; stopping pagination.",
                    _options.MaxPagesToFetch);
                break;
            }
        }

        var games = FilterGamePages(allPages, _options.GameSlugSuffix);
        Logger.LogInformation(
            "Pinball Brothers: {Total} WP pages scanned, {Games} look like game pages.",
            allPages.Count, games.Count);
        return games;
    }

    /// <summary>
    /// Parses a WP REST <c>/pages</c> response body. Returns an empty
    /// list rather than throwing if the body is empty or malformed.
    /// </summary>
    public static List<PbPageRaw> ParsePagesJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<PbPageRaw>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns the subset of <paramref name="pages"/> whose slug ends
    /// with <paramref name="suffix"/> (case-insensitive). The trailing
    /// suffix is the project's signal that a page is a Pinball
    /// Brothers game page.
    /// </summary>
    public static List<PbPageRaw> FilterGamePages(IEnumerable<PbPageRaw> pages, string suffix)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);

        var result = new List<PbPageRaw>();
        foreach (var page in pages)
        {
            if (!string.IsNullOrEmpty(page.Slug)
                && page.Slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(page);
            }
        }
        return result;
    }

    private Uri BuildPagesUrl(int page)
    {
        var baseUri = new Uri(_options.BaseUrl);
        var fields = "id,slug,link,parent,modified,title";
        var path = _options.PagesEndpointPath
            + $"?per_page={_options.PageSize}&page={page}&_fields={fields}";
        return new Uri(baseUri, path);
    }
}
