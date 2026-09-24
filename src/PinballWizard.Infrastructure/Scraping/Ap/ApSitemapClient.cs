using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Ap;

/// <summary>
/// Discovers American Pinball game-page URLs from the sitemap.
/// </summary>
/// <remarks>
/// The historical sitemap was a flat urlset of <c>/games/{slug}</c>
/// pages. Yoast now redirects <c>/sitemap.xml</c> to a sitemap index
/// whose child urlsets list every post at <c>/{slug}/</c>, including
/// manuals and news. Game pages are the posts in the WordPress
/// <c>game-page</c> category — the same role JJP's pinball-machine
/// collection plays for <c>/products/{handle}</c>. Legacy
/// <c>/games/{slug}</c> URLs are still accepted. Sitemap-first
/// discovery is preferred over DOM scraping per the locked feedback
/// memory <c>feedback_machine_consumer_metadata_first.md</c>.
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
    /// Returns the game-page URLs discovered from the sitemap.
    /// A successful fetch that yields none is an error: an empty set
    /// is not a completed scrape.
    /// </summary>
    public async Task<List<Uri>> DiscoverGameUrlsAsync(CancellationToken cancellationToken)
    {
        var sitemapUrl = new Uri(new Uri(_options.BaseUrl), _options.SitemapPath);
        Logger.LogInformation("AP: reading sitemap at {Url}", sitemapUrl);

        var body = await GetStringPolitelyAsync(_httpClient, sitemapUrl, cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveGameUrlsAsync(sitemapUrl, body, cancellationToken).ConfigureAwait(false);
        var urls = new List<Uri>(resolved.Count);
        foreach (var url in resolved)
        {
            if (IsAllowedSitemapUrl(url))
            {
                urls.Add(url);
            }
            else
            {
                Logger.LogWarning(
                    "AP: skipping game URL {Url}; host is not an allowed AP sitemap host.",
                    url);
            }
        }

        if (urls.Count == 0)
        {
            Logger.LogError(
                "AP: sitemap discovery returned 0 game-page URLs from {Url}. The document was fetched but no game page matched.",
                sitemapUrl);
            throw new InvalidOperationException(
                $"AP sitemap discovery at {sitemapUrl} returned 0 game-page URLs.");
        }

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

        foreach (var url in UrlElements(doc))
        {
            var loc = LocValue(url);
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

    /// <summary>
    /// True when the document element is a sitemap index rather than a urlset.
    /// </summary>
    public static bool IsSitemapIndex(string sitemapXml)
    {
        ArgumentNullException.ThrowIfNull(sitemapXml);
        var doc = XDocument.Parse(sitemapXml);
        return doc.Root?.Name.LocalName.Equals("sitemapindex", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Returns the absolute child-sitemap URLs listed in a sitemap index.
    /// </summary>
    public static List<Uri> ParseChildSitemapUrls(string indexXml)
    {
        ArgumentNullException.ThrowIfNull(indexXml);
        var doc = XDocument.Parse(indexXml);
        var urls = new List<Uri>();
        foreach (var sitemap in NamedElements(doc, "sitemap"))
        {
            var loc = LocValue(sitemap);
            if (string.IsNullOrWhiteSpace(loc)) continue;
            if (Uri.TryCreate(loc.Trim(), UriKind.Absolute, out var uri))
            {
                urls.Add(uri);
            }
        }

        return urls;
    }

    /// <summary>
    /// Returns the category id whose <c>slug</c> matches
    /// <paramref name="categorySlug"/>, or null when the collection
    /// does not contain it. A non-array body throws.
    /// </summary>
    public static int? ParseGamePageCategoryId(string json, string categorySlug)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(categorySlug);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("AP categories response was not a JSON array.");
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("slug", out var slugElement)) continue;
            if (!string.Equals(slugElement.GetString(), categorySlug, StringComparison.OrdinalIgnoreCase)) continue;
            if (item.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>(slug, link)</c> pairs from a WordPress posts page.
    /// Entries missing either field are skipped. A non-array body throws.
    /// </summary>
    public static List<(string Slug, Uri Link)> ParseGamePagePosts(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("AP posts response was not a JSON array.");
        }

        var posts = new List<(string Slug, Uri Link)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var slug = item.TryGetProperty("slug", out var slugElement) ? slugElement.GetString() : null;
            var link = item.TryGetProperty("link", out var linkElement) ? linkElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(link)) continue;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) continue;
            posts.Add((slug, uri));
        }

        return posts;
    }

    /// <summary>
    /// Combines legacy <c>/games/{slug}</c> URLs with the sitemap
    /// locs whose single path segment is a game-page category slug.
    /// A category link that the sitemap has not listed yet is kept.
    /// Manuals, news, and nested support URLs are not.
    /// </summary>
    public static List<Uri> SelectGameUrls(
        IEnumerable<Uri> legacyGameUrls,
        IEnumerable<Uri> sitemapLocs,
        IEnumerable<(string Slug, Uri Link)> categoryPosts)
    {
        ArgumentNullException.ThrowIfNull(legacyGameUrls);
        ArgumentNullException.ThrowIfNull(sitemapLocs);
        ArgumentNullException.ThrowIfNull(categoryPosts);

        var categorySlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categoryLinks = new List<(string Slug, Uri Link)>();
        foreach (var post in categoryPosts)
        {
            if (string.IsNullOrWhiteSpace(post.Slug)) continue;
            categorySlugs.Add(post.Slug);
            categoryLinks.Add(post);
        }

        var chosen = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var legacy in legacyGameUrls)
        {
            var slug = LegacyGameSlug(legacy);
            if (slug is null) continue;
            Consider(chosen, order, slug, legacy, overwrite: false);
        }

        foreach (var loc in sitemapLocs)
        {
            if (!TryGetPermalinkSlug(loc, out var slug)) continue;
            if (!categorySlugs.Contains(slug)) continue;
            // The sitemap loc is the published URL. Prefer it over a legacy path.
            Consider(chosen, order, slug, loc, overwrite: true);
        }

        foreach (var post in categoryLinks)
        {
            if (!categorySlugs.Contains(post.Slug)) continue;
            if (!TryGetPermalinkSlug(post.Link, out var linkSlug)) continue;
            if (!string.Equals(linkSlug, post.Slug, StringComparison.OrdinalIgnoreCase)) continue;
            Consider(chosen, order, post.Slug, post.Link, overwrite: false);
        }

        var urls = new List<Uri>(order.Count);
        foreach (var slug in order)
        {
            urls.Add(chosen[slug]);
        }

        return urls;
    }

    private async Task<List<Uri>> ResolveGameUrlsAsync(
        Uri sitemapUrl,
        string body,
        CancellationToken cancellationToken)
    {
        if (!IsSitemapIndex(body))
        {
            return ParseGameUrls(body, _options.GamePathPrefix);
        }

        var children = ParseChildSitemapUrls(body);
        Logger.LogInformation("AP: sitemap index references {Count} child sitemap(s)", children.Count);
        if (children.Count == 0)
        {
            throw new InvalidOperationException(
                $"AP sitemap index at {sitemapUrl} contained no child sitemaps.");
        }

        var allowed = new List<Uri>();
        foreach (var child in children)
        {
            if (IsAllowedSitemapUrl(child))
            {
                allowed.Add(child);
            }
            else
            {
                Logger.LogWarning(
                    "AP: skipping child sitemap {Url}; host is not an allowed AP sitemap host.",
                    child);
            }
        }

        if (allowed.Count == 0)
        {
            throw new InvalidOperationException(
                $"AP sitemap index at {sitemapUrl} listed {children.Count} child sitemap(s) but none were on an allowed host.");
        }

        var legacy = new List<Uri>();
        var locs = new List<Uri>();
        foreach (var child in allowed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childBody = await GetStringPolitelyAsync(_httpClient, child, cancellationToken).ConfigureAwait(false);
            if (IsSitemapIndex(childBody))
            {
                throw new InvalidOperationException(
                    $"AP child sitemap {child} is itself an index; refusing to guess which descendant lists game pages.");
            }

            var childLocs = ParseAllLocs(childBody);
            legacy.AddRange(ParseGameUrls(childBody, _options.GamePathPrefix));
            locs.AddRange(childLocs);
            Logger.LogInformation(
                "AP: child sitemap {Url} listed {Count} URL(s)",
                child, childLocs.Count);
        }

        var posts = await ReadGamePagePostsAsync(cancellationToken).ConfigureAwait(false);
        if (posts is null)
        {
            // A missing category is not "no games." Returning any leftover
            // /games/{slug} URLs would hide that and look like a small success.
            Logger.LogError(
                "AP: WordPress category {Slug} was not found. Refusing to treat legacy /games/ URLs as the catalog ({LegacyCount} such URL(s) were in the sitemap).",
                _options.GamePageCategorySlug, legacy.Count);
            throw new InvalidOperationException(
                $"AP sitemap index at {sitemapUrl} has no WordPress category '{_options.GamePageCategorySlug}'.");
        }

        Logger.LogInformation(
            "AP: WordPress category {Slug} contains {Count} game-page post(s)",
            _options.GamePageCategorySlug, posts.Count);

        return SelectGameUrls(legacy, locs, posts);
    }

    /// <summary>
    /// Reads the game-page category. Returns null when the category
    /// slug is absent. A transport or shape failure propagates.
    /// </summary>
    private async Task<List<(string Slug, Uri Link)>?> ReadGamePagePostsAsync(CancellationToken cancellationToken)
    {
        var categoriesUrl = BuildCategoriesUrl();
        Logger.LogInformation("AP: reading WordPress category {Slug} at {Url}", _options.GamePageCategorySlug, categoriesUrl);
        var categoriesJson = await GetStringPolitelyAsync(_httpClient, categoriesUrl, cancellationToken).ConfigureAwait(false);
        var categoryId = ParseGamePageCategoryId(categoriesJson, _options.GamePageCategorySlug);
        if (categoryId is null)
        {
            return null;
        }

        var posts = new List<(string Slug, Uri Link)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageSize = Math.Clamp(_options.CategoryPageSize, 1, 100);
        var maxPages = Math.Max(1, _options.MaxCategoryPages);

        for (var page = 1; page <= maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var postsUrl = BuildPostsUrl(categoryId.Value, page, pageSize);
            using var request = new HttpRequestMessage(HttpMethod.Get, postsUrl);
            using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var postsJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var batch = ParseGamePagePosts(postsJson);

            foreach (var post in batch)
            {
                if (seen.Add(post.Slug))
                {
                    posts.Add(post);
                }
            }

            // WordPress returns 400 rest_post_invalid_page_number for a page
            // past the end. A full page is not proof another page exists.
            var totalPages = ReadTotalPages(response);
            if (totalPages is int known && page >= known)
            {
                break;
            }

            if (totalPages is null)
            {
                if (batch.Count >= pageSize)
                {
                    Logger.LogWarning(
                        "AP: posts response for category {Slug} omitted X-WP-TotalPages on a full page; not requesting another page.",
                        _options.GamePageCategorySlug);
                }

                break;
            }

            if (page == maxPages)
            {
                Logger.LogWarning(
                    "AP: MaxCategoryPages ({Cap}) reached while reading category {Slug}; additional game pages were not requested.",
                    maxPages, _options.GamePageCategorySlug);
            }
        }

        return posts;
    }

    private static int? ReadTotalPages(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-WP-TotalPages", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();
        return int.TryParse(raw, out var total) && total >= 1 ? total : null;
    }

    private Uri BuildCategoriesUrl()
    {
        var endpoint = new Uri(new Uri(_options.BaseUrl), _options.CategoriesEndpointPath);
        return new Uri($"{endpoint}?slug={Uri.EscapeDataString(_options.GamePageCategorySlug)}&_fields=id,slug");
    }

    private Uri BuildPostsUrl(int categoryId, int page, int pageSize)
    {
        var endpoint = new Uri(new Uri(_options.BaseUrl), _options.PostsEndpointPath);
        return new Uri($"{endpoint}?categories={categoryId}&per_page={pageSize}&page={page}&_fields=slug,link");
    }

    private bool IsAllowedSitemapUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            hosts.Add(baseUri.Host);
        }

        if (_options.SitemapHosts is not null)
        {
            foreach (var host in _options.SitemapHosts)
            {
                if (!string.IsNullOrWhiteSpace(host))
                {
                    hosts.Add(host.Trim());
                }
            }
        }

        return hosts.Contains(uri.Host);
    }

    private static List<Uri> ParseAllLocs(string sitemapXml)
    {
        var doc = XDocument.Parse(sitemapXml);
        var urls = new List<Uri>();
        foreach (var url in UrlElements(doc))
        {
            var loc = LocValue(url);
            if (string.IsNullOrWhiteSpace(loc)) continue;
            if (Uri.TryCreate(loc.Trim(), UriKind.Absolute, out var uri))
            {
                urls.Add(uri);
            }
        }

        return urls;
    }

    private static string? LegacyGameSlug(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2
            && segments[0].Equals("games", StringComparison.OrdinalIgnoreCase))
        {
            return segments[1];
        }

        return null;
    }

    private static bool TryGetPermalinkSlug(Uri uri, out string slug)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1
            && !segments[0].Equals("games", StringComparison.OrdinalIgnoreCase))
        {
            slug = segments[0];
            return true;
        }

        slug = string.Empty;
        return false;
    }

    private static void Consider(
        Dictionary<string, Uri> chosen,
        List<string> order,
        string slug,
        Uri uri,
        bool overwrite)
    {
        if (!chosen.ContainsKey(slug))
        {
            chosen[slug] = uri;
            order.Add(slug);
            return;
        }

        if (overwrite)
        {
            chosen[slug] = uri;
        }
    }

    private static List<XElement> UrlElements(XContainer container) => NamedElements(container, "url");

    private static List<XElement> NamedElements(XContainer container, string localName)
    {
        var namespaced = container.Descendants(SitemapNs + localName).ToList();
        if (namespaced.Count > 0)
        {
            return namespaced;
        }

        return container.Descendants()
            .Where(element => element.Name.LocalName.Equals(localName, StringComparison.Ordinal))
            .ToList();
    }

    private static string? LocValue(XElement parent)
    {
        var namespaced = parent.Element(SitemapNs + "loc")?.Value;
        if (!string.IsNullOrWhiteSpace(namespaced))
        {
            return namespaced.Trim();
        }

        return parent.Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals("loc", StringComparison.Ordinal))
            ?.Value
            ?.Trim();
    }
}
