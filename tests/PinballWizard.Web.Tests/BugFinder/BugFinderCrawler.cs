using Microsoft.Playwright;

namespace PinballWizard.Web.Tests.BugFinder;

// Discovers all public pages to crawl:
//   1. Starts with the known public seed routes (from PublicRouteCanaryE2ETests)
//   2. Walks same-origin <a> links found on each page
//   3. Samples deep-link routes for /documents/{id} and /wizard/q/{slug}
//
// Excludes: /admin/*, /error*, /tilt, already-visited URLs.
// Cap: BugFinderCrawler.MaxPages (default 100).
public sealed class BugFinderCrawler
{
    public const int MaxPages = 100;

    private static readonly string[] SeedRoutes =
    [
        "/",
        "/about",
        "/documents",
        "/settings",
        "/status",
        "/auth-demo",
    ];

    private static readonly string[] ExcludePrefixes =
    [
        "/admin",
        "/error",
        "/tilt",
        "/_blazor",
        "/_framework",
        "/favicon",
        "/robots",
        "/sitemap",
    ];

    private static readonly string[] ExcludeExtensions =
    [
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".svg",
        ".css", ".js", ".woff", ".woff2", ".ico", ".map",
    ];

    private readonly string _baseUrl;
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _queue = new();

    public BugFinderCrawler(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        foreach (var route in SeedRoutes)
        {
            Enqueue(route);
        }
    }

    public IEnumerable<string> DrainQueue() => _queue;

    // Discovers additional links from a visited page and enqueues them.
    public async Task DiscoverLinksAsync(IPage page, string currentUrl)
    {
        if (_visited.Count >= MaxPages) return;

        try
        {
            var hrefs = await page.EvaluateAsync<string[]>("""
                () => Array.from(document.querySelectorAll('a[href]'))
                           .map(a => a.getAttribute('href'))
                           .filter(h => h && !h.startsWith('#') && !h.startsWith('mailto:') && !h.startsWith('tel:'))
                """);

            foreach (var href in hrefs ?? [])
            {
                var normalized = Normalize(href);
                if (normalized is not null)
                {
                    Enqueue(normalized);
                }
            }

            // Sample document deep links from the document grid
            await SampleDocumentLinksAsync(page);
        }
        catch
        {
            // Link discovery is best-effort — never crash the crawl
        }
    }

    public bool TryDequeue(out string url)
    {
        while (_queue.Count > 0)
        {
            url = _queue.Dequeue();
            if (!_visited.Contains(url) && _visited.Count < MaxPages)
            {
                _visited.Add(url);
                return true;
            }
        }
        url = string.Empty;
        return false;
    }

    public void MarkVisited(string url) => _visited.Add(url);

    public int VisitedCount => _visited.Count;

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Enqueue(string relativeOrAbsolute)
    {
        var normalized = Normalize(relativeOrAbsolute);
        if (normalized is not null && !_visited.Contains(normalized))
        {
            _queue.Enqueue(normalized);
        }
    }

    private string? Normalize(string href)
    {
        // Resolve absolute and relative URLs; reject external
        string absolute;
        if (href.StartsWith("http://") || href.StartsWith("https://"))
        {
            if (!href.StartsWith(_baseUrl, StringComparison.OrdinalIgnoreCase))
                return null; // external link
            absolute = href;
        }
        else if (href.StartsWith('/'))
        {
            absolute = _baseUrl + href;
        }
        else
        {
            return null; // relative paths like "./foo" — skip
        }

        // Strip query + fragment for deduplication
        if (Uri.TryCreate(absolute, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath;

            // Extension filter
            if (ExcludeExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                return null;

            // Prefix filter
            if (ExcludePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return null;

            return _baseUrl + path;
        }
        return null;
    }

    private async Task SampleDocumentLinksAsync(IPage page)
    {
        // Grab up to 10 document card links from the /documents page
        try
        {
            var docLinks = await page.EvaluateAsync<string[]>("""
                () => Array.from(document.querySelectorAll('[data-testid="doc-card"] a, [data-testid="doc-list-grid"] a'))
                           .slice(0, 10)
                           .map(a => a.getAttribute('href'))
                           .filter(Boolean)
                """);

            foreach (var link in docLinks ?? [])
            {
                Enqueue(link);
            }
        }
        catch { /* best-effort */ }
    }
}
