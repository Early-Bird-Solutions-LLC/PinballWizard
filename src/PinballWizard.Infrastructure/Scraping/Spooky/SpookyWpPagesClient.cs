using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Spooky;

/// <summary>
/// Reads Spooky Pinball's WordPress REST API to enumerate pages and
/// returns the subset that look like individual game pages. Discovery
/// via the REST API is preferred over HTML scraping per the locked
/// feedback memory <c>feedback_machine_consumer_metadata_first.md</c>.
/// </summary>
/// <remarks>
/// "Looks like a game page" means the page's content body contains
/// firmware-download URLs at Spooky's S3 host whose first path segments
/// resolve to one or two distinct game slugs. One slug is the common
/// case; two indicates a shared-hardware page (e.g. Halloween+Ultraman on
/// the Pinotaur platform). Aggregator pages — e.g., a base-image-update
/// notice listing firmware for three or more games — carry three or more
/// distinct slugs, fail the check, and are excluded.
/// </remarks>
public sealed class SpookyWpPagesClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;
    private readonly SpookyOptions _options;

    /// <summary>Initializes a new <see cref="SpookyWpPagesClient"/>.</summary>
    public SpookyWpPagesClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<SpookyOptions> spookyOptions,
        ILogger<SpookyWpPagesClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(spookyOptions);
        _httpClient = httpClient;
        _options = spookyOptions.Value;
    }

    /// <summary>
    /// Enumerates Spooky's WP pages (paginated) and returns the subset
    /// that pass the single-S3-slug game filter.
    /// </summary>
    public async Task<List<SpookyPageRaw>> DiscoverGamePagesAsync(CancellationToken cancellationToken)
    {
        var allPages = new List<SpookyPageRaw>();
        int page = 1;
        while (true)
        {
            var url = BuildPagesUrl(page);
            Logger.LogInformation("Spooky: reading WP pages {Url}", url);

            var body = await GetStringPolitelyAsync(_httpClient, url, cancellationToken).ConfigureAwait(false);
            var batch = ParsePagesJson(body);
            if (batch.Count == 0)
            {
                break;
            }

            allPages.AddRange(batch);

            // Stop conditions: fewer items than per_page means we're on the last page.
            if (batch.Count < _options.PageSize)
            {
                break;
            }

            page++;
            if (page > _options.MaxPagesToFetch)
            {
                Logger.LogWarning(
                    "Spooky: MaxPagesToFetch ({Cap}) reached; stopping pagination.",
                    _options.MaxPagesToFetch);
                break;
            }
        }

        var games = FilterGamePages(allPages, _options.S3Host);
        Logger.LogInformation(
            "Spooky: {Total} WP pages scanned, {Games} look like game pages.",
            allPages.Count, games.Count);
        return games;
    }

    /// <summary>
    /// Parses a WP REST <c>/pages</c> response body. Returns an empty
    /// list rather than throwing if the body is empty / not an array.
    /// </summary>
    public static List<SpookyPageRaw> ParsePagesJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<SpookyPageRaw>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns the subset of <paramref name="pages"/> whose content
    /// contains at least one S3 URL at <paramref name="s3Host"/> AND
    /// whose S3 URLs share one or two distinct first-path-segment slugs
    /// (the game slug(s)). Two-slug pages are shared-hardware pages
    /// (e.g., Halloween+Ultraman on the Pinotaur platform). Pages with
    /// three or more distinct slugs are aggregator/update notices and
    /// are excluded.
    /// </summary>
    public static List<SpookyPageRaw> FilterGamePages(IEnumerable<SpookyPageRaw> pages, string s3Host)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentException.ThrowIfNullOrWhiteSpace(s3Host);

        var result = new List<SpookyPageRaw>();
        foreach (var page in pages)
        {
            var slugs = ExtractS3Slugs(page.Content.Rendered, s3Host);
            if (slugs.Count is >= 1 and <= 2)
            {
                result.Add(page);
            }
        }
        return result;
    }

    /// <summary>
    /// Pulls the set of distinct first-path-segment slugs from S3 URLs
    /// in <paramref name="html"/>. A "single distinct slug" page is a
    /// game page; "many distinct slugs" is an aggregator/update page.
    /// </summary>
    public static HashSet<string> ExtractS3Slugs(string html, string s3Host)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(s3Host);

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return slugs;

        var pattern = @"https?://" + Regex.Escape(s3Host) + @"/([^/?#""<>\s]+)";
        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
        {
            var slug = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(slug))
            {
                slugs.Add(slug);
            }
        }
        return slugs;
    }

    private Uri BuildPagesUrl(int page)
    {
        var baseUri = new Uri(_options.BaseUrl);
        var fields = "id,slug,link,parent,modified,title,content";
        var path = _options.PagesEndpointPath
            + $"?per_page={_options.PageSize}&page={page}&_fields={fields}";
        return new Uri(baseUri, path);
    }
}
