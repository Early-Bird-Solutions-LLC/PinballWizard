using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// Regression guard for the floating-footer bug (reported browsing the live app
// 2026-07-11): on a short public page (/status, /about on a tall viewport) the
// BrandFooter rendered immediately under the content and left a band of dead
// space beneath it, instead of sitting at the bottom of the viewport.
//
// Root cause: .brand-footer already carried `margin-top: auto` — the second half
// of the conventional sticky-footer idiom — but its parent (MudLayout's root) was
// a plain block box with no height, so there was never any free space for the auto
// margin to absorb. MainLayout now applies .pw-sticky-footer (app.css): a flex
// column with min-height: 100dvh.
//
// Why this lives in the E2E suite and not the a11y/snapshot Playwright suite:
// PlaywrightWebApplicationFactory is a minimal host that serves NO stylesheets
// (its own comments say so; screenshots from it are unstyled). A layout assertion
// there would measure an unstyled document and pass no matter what the CSS says.
// This suite drives the real deployed app (deploy.yml E2E canary), where app.css
// and the MudBlazor chrome are actually loaded — the only place the assertion means
// anything.
//
// Measured live on the fix branch (1440×1500, /error): footer bottom 836px with the
// class removed (664px of dead space) vs 1500px with it (flush to the viewport).
[Collection("E2E live stack")]
[Trait("Category", "E2E")]
public sealed class PublicStickyFooterE2ETests : IAsyncLifetime
{
    // Deliberately tall: it makes the "short page" cases unambiguously shorter than
    // the viewport, which is the only state in which the bug is observable. At a
    // normal 900px height some of these pages fill the fold and the footer is
    // pinned by content alone — a false pass.
    private const int ViewportWidth = 1440;
    private const int ViewportHeight = 1500;

    // Sub-pixel slack: layout maths (dvh → fractional px, MudBlazor's border box)
    // lands the footer edge within a pixel of the viewport edge, not exactly on it.
    private const int TolerancePx = 2;

    private readonly LiveStackFixture _stack;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PublicStickyFooterE2ETests(LiveStackFixture stack) => _stack = stack;

    public async Task InitializeAsync()
    {
        if (!E2EFactAttribute.IsConfigured)
            return;

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    // Short pages: the footer's bottom edge must land ON the viewport's bottom edge,
    // with the page not scrolling. This is the actual reported defect.
    [E2ETheory]
    [InlineData("/status")]
    [InlineData("/error")]
    public async Task ShortPage_PinsFooterToViewportBottom(string route)
    {
        var page = await NewPageAsync();
        await GotoAsync(page, route);

        var footer = page.Locator("footer.brand-footer");
        await footer.WaitForAsync(new() { Timeout = 20_000 });

        var box = await footer.BoundingBoxAsync()
            ?? throw new InvalidOperationException($"BrandFooter is not visible on {route}.");
        var footerBottom = box.Y + box.Height;

        // Guard the premise: if the page has grown tall enough to scroll, it is no
        // longer a "short page" and this case proves nothing. Fail loudly rather
        // than pass vacuously — the fixture, not the layout, is what's stale.
        var scrolls = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollHeight > window.innerHeight + 1");
        Assert.False(scrolls,
            $"{route} now scrolls at {ViewportWidth}×{ViewportHeight}, so it can no longer " +
            "exercise the sticky-footer case. Pick a shorter route or a taller viewport — " +
            "do not delete this assertion, it is what keeps the test honest.");

        Assert.True(
            Math.Abs(footerBottom - ViewportHeight) <= TolerancePx,
            $"BrandFooter floats on the short page {route}: its bottom edge is at {footerBottom:F0}px " +
            $"but the viewport bottom is {ViewportHeight}px — {ViewportHeight - footerBottom:F0}px of dead " +
            "space below the footer. MainLayout's .pw-sticky-footer (flex column + min-height: 100dvh, " +
            "app.css) is missing or overridden, so .brand-footer's `margin-top: auto` has no free space " +
            "to absorb.");
    }

    // Tall page: the sticky-footer layout must not change how a long page behaves —
    // the footer still flows AFTER the content (below the fold), never overlapping it
    // and never pinned to the viewport as a fixed bar.
    [E2EFact]
    public async Task TallPage_FooterFlowsAfterContent()
    {
        var page = await NewPageAsync();
        await GotoAsync(page, "/about");

        var footer = page.Locator("footer.brand-footer");
        await footer.WaitForAsync(new() { Timeout = 20_000 });

        // .mud-main-content is a MudBlazor-internal class, not a public API — re-verify
        // after any MudBlazor major-version bump (same caveat app.css carries for
        // .column-header).
        var geometry = await page.EvaluateAsync<FooterGeometry>(
            """
            () => {
                const footer = document.querySelector('footer.brand-footer');
                const content = document.querySelector('.mud-main-content');
                const f = footer.getBoundingClientRect();
                return {
                    footerTop: f.top + window.scrollY,
                    contentBottom: content.getBoundingClientRect().bottom + window.scrollY,
                    documentHeight: document.documentElement.scrollHeight,
                    viewportHeight: window.innerHeight,
                };
            }
            """);

        // Premise: /about must actually overflow the viewport, or this proves nothing.
        Assert.True(geometry.DocumentHeight > geometry.ViewportHeight,
            $"/about no longer overflows a {ViewportHeight}px viewport ({geometry.DocumentHeight}px tall), " +
            "so it cannot exercise the tall-page case. Pick a longer route.");

        // The footer starts at or after the content ends — no overlap, no fixed bar
        // sitting on top of the content.
        Assert.True(geometry.FooterTop >= geometry.ContentBottom - TolerancePx,
            $"BrandFooter overlaps page content on /about: footer top {geometry.FooterTop:F0}px is above " +
            $"the content bottom {geometry.ContentBottom:F0}px. The sticky-footer layout must leave tall " +
            "pages flowing normally.");
    }

    private async Task<IPage> NewPageAsync()
    {
        var ctx = await _browser!.NewContextAsync(new()
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
        });
        return await ctx.NewPageAsync();
    }

    private Task<IResponse?> GotoAsync(IPage page, string route) =>
        page.GotoAsync($"{_stack.WebBaseUrl}{route}",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });

    // Plain settable class, not a positional record: Playwright's evaluate-result
    // converter instantiates the target type through its parameterless constructor,
    // which a positional record does not have (it throws MissingMethodException).
    private sealed class FooterGeometry
    {
        public double FooterTop { get; set; }
        public double ContentBottom { get; set; }
        public double DocumentHeight { get; set; }
        public double ViewportHeight { get; set; }
    }
}
