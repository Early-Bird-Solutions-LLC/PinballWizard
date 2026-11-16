using Microsoft.Playwright;

namespace PinballWizard.Web.Tests.BugFinder;

// Per-page functional checks: console errors, network failures, error-page
// redirects, missing expected surfaces, load time, meta tags, broken links.
public static class FunctionalChecks
{
    // Known non-critical console noise from third-party scripts / Blazor internals.
    private static readonly string[] SuppressedConsolePatterns =
    [
        "Download the React DevTools",    // any embedded React widgets
        "Content Security Policy",        // CSP reports are expected in dev
        "[HMR]",                          // hot-module reload noise
        "Failed to load resource: net::ERR_BLOCKED_BY_CLIENT", // ad-blocker false positives
    ];

    public static async Task<List<BugFinding>> CheckPageAsync(
        IPage page,
        string url,
        TimeSpan loadTime)
    {
        var findings = new List<BugFinding>();

        // 1. Error-page redirect
        if (page.Url.Contains("/error", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new BugFinding(url, BugSeverity.Critical, BugSource.Functional,
                "Page redirected to the global /error route",
                $"Navigated to {url} but ended on {page.Url}. A Blazor render exception was thrown."));
        }

        // 2. TILT surfaces that replace page content without a URL change:
        //    - tilt-boundary-fallback: in-layout TiltErrorBoundary
        //    - tilt-heading: Error.razor re-executed via UseExceptionHandler("/error")
        //      while the browser URL stays on the original route (Bugbot #1077).
        // Skip when the crawler intentionally opened /error or /tilt.
        var onDedicatedTiltRoute = url.Contains("/error", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/tilt", StringComparison.OrdinalIgnoreCase);
        if (!onDedicatedTiltRoute)
        {
            var tiltSurface = page.Locator(
                "[data-testid='tilt-boundary-fallback'], [data-testid='tilt-heading']");
            if (await tiltSurface.CountAsync() > 0 && await tiltSurface.First.IsVisibleAsync())
            {
                findings.Add(new BugFinding(url, BugSeverity.Critical, BugSource.Functional,
                    "Global Tilt error surface rendered instead of page content",
                    "tilt-boundary-fallback or tilt-heading is visible — ErrorBoundary / "
                    + "UseExceptionHandler replaced the page without a navigated /error URL."));
            }
        }

        // 3. Load time
        if (loadTime > TimeSpan.FromSeconds(5))
        {
            findings.Add(new BugFinding(url, BugSeverity.Medium, BugSource.Functional,
                $"Slow page load: {loadTime.TotalSeconds:F1}s",
                "Pages should load within 5s. Check for slow Cosmos/AI Search queries or large asset bundles."));
        }

        // 4. Missing <title>
        var title = await page.TitleAsync();
        if (string.IsNullOrWhiteSpace(title) || title.Equals("Index", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new BugFinding(url, BugSeverity.Low, BugSource.Functional,
                $"Missing or generic <title>: \"{title}\"",
                "Every page should have a descriptive title for SEO and browser tab identification."));
        }

        // 5. Missing meta description
        var metaDesc = await page.EvaluateAsync<string?>(
            "() => document.querySelector('meta[name=\"description\"]')?.getAttribute('content')");
        if (string.IsNullOrWhiteSpace(metaDesc))
        {
            findings.Add(new BugFinding(url, BugSeverity.Low, BugSource.Functional,
                "Missing <meta name=\"description\">",
                "Meta descriptions improve SEO and link previews."));
        }

        return findings;
    }

    // Attaches console + response listeners before navigation; returns a
    // callback that extracts accumulated findings after navigation completes.
    public static (Action attach, Func<string, List<BugFinding>> harvest)
        BuildPageListeners(IPage page)
    {
        var consoleErrors = new List<(string type, string text)>();
        var networkFailures = new List<string>();

        void attach()
        {
            page.Console += (_, msg) =>
            {
                if (msg.Type is "error" or "warning")
                {
                    var text = msg.Text;
                    if (!SuppressedConsolePatterns.Any(p =>
                            text.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        lock (consoleErrors) consoleErrors.Add((msg.Type, text));
                    }
                }
            };

            page.Response += (_, response) =>
            {
                var status = response.Status;
                var respUrl = response.Url;
                // Only flag same-origin failures (not CDN/analytics/fonts)
                if (status is >= 400 and not 404 || (status == 404 && respUrl.Contains("/api/")))
                {
                    lock (networkFailures)
                        networkFailures.Add($"HTTP {status} — {respUrl}");
                }
            };
        }

        List<BugFinding> harvest(string pageUrl)
        {
            var findings = new List<BugFinding>();

            foreach (var (type, text) in consoleErrors.Take(5))
            {
                var severity = type == "error" ? BugSeverity.High : BugSeverity.Medium;
                findings.Add(new BugFinding(pageUrl, severity, BugSource.Functional,
                    $"Browser console {type}: {Truncate(text, 120)}",
                    text));
            }

            foreach (var failure in networkFailures.Take(5))
            {
                findings.Add(new BugFinding(pageUrl, BugSeverity.High, BugSource.Functional,
                    $"Network failure: {Truncate(failure, 120)}",
                    failure));
            }

            return findings;
        }

        return (attach, harvest);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
