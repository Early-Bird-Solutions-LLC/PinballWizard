using Microsoft.Playwright;

namespace PinballWizard.Web.Tests.Snapshots;

// Discovers internal app routes from live nav + footer chrome (#790 companion).
// Hard-coded InlineData lists drift the day a page ships without a matching row;
// crawling the chrome that users actually see keeps coverage tied to navigation.
internal static class NavRouteDiscovery
{
    // Paths that are chrome destinations but need a different host (admin auth /
    // interactive circuits) or are non-page anchors — excluded from public SSR
    // invariant sweeps.
    private static readonly HashSet<string> PublicExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "/admin",
    };

    public static async Task<IReadOnlyList<string>> DiscoverFromPublicChromeAsync(
        IPage page,
        string serverAddress)
    {
        await page.GotoAsync(
            $"{serverAddress.TrimEnd('/')}/",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var paths = await CollectInternalPathsAsync(page);
        return paths
            .Where(p => !PublicExclusions.Contains(p)
                        && !p.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static async Task<IReadOnlyList<string>> DiscoverFromAdminNavAsync(
        IPage page,
        string serverAddress)
    {
        await page.GotoAsync(
            $"{serverAddress.TrimEnd('/')}/admin",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Prefer the rail menu; fall back to any /admin internal link if MudBlazor
        // wraps the anchors without .mud-nav-menu in a given SSR path.
        var paths = await page.EvaluateAsync<string[]>(
            """
            () => {
              const hrefs = new Set();
              const nodes = document.querySelectorAll(
                '.mud-nav-menu a[href], .mud-nav-item a[href], a[href^="/admin"]');
              for (const a of nodes) {
                const raw = a.getAttribute('href') || '';
                if (!raw || raw.startsWith('http') || raw.startsWith('mailto:') || raw.startsWith('#'))
                  continue;
                let path = raw.split('#')[0].split('?')[0];
                if (!path.startsWith('/admin')) continue;
                if (path.length > 1 && path.endsWith('/')) path = path.slice(0, -1);
                hrefs.add(path);
              }
              return Array.from(hrefs).sort();
            }
            """);

        return paths;
    }

    private static async Task<IReadOnlyList<string>> CollectInternalPathsAsync(IPage page)
    {
        // Rail (.mud-nav-menu) + footer nav — the surfaces #790's companion names.
        var paths = await page.EvaluateAsync<string[]>(
            """
            () => {
              const hrefs = new Set();
              const nodes = document.querySelectorAll(
                '.mud-nav-menu a[href], footer a[href], .brand-footer a[href]');
              for (const a of nodes) {
                const raw = a.getAttribute('href') || '';
                if (!raw || raw.startsWith('http') || raw.startsWith('mailto:') || raw.startsWith('#'))
                  continue;
                let path = raw.split('#')[0].split('?')[0];
                if (!path.startsWith('/')) continue;
                if (path.length > 1 && path.endsWith('/')) path = path.slice(0, -1);
                hrefs.add(path);
              }
              return Array.from(hrefs).sort();
            }
            """);

        return paths;
    }
}
