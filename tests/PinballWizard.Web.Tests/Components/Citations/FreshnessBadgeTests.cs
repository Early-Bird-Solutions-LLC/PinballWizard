using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Citations;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Citations;

// Behavioral tests for FreshnessBadge.
//
// The stale-amber visual pin (>= 90 days → freshness-stale CSS class) is a
// behavioral contract per ADR-0026 § 4. These tests assert the actual rendered
// data-freshness-class attribute so a future token rename or threshold change
// that breaks the visual intent fails the build.
//
// All tests use bUnit rendering and assert rendered markup; no InternalsVisibleTo
// required since no private/internal members are accessed directly.
public sealed class FreshnessBadgeTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Helper
    // ──────────────────────────────────────────────────────────────────────

    private static TestContext BuildCtx()
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Label: "3 days ago" for recent timestamps
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_3_days_ago_for_recent_timestamp()
    {
        using var ctx = BuildCtx();

        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var ts  = now.AddDays(-3);

        var cut = ctx.RenderComponent<FreshnessBadge>(p => p
            .Add(c => c.LastScrapedUtc, ts)
            .Add(c => c.Now, now));

        var badge = cut.Find("[data-testid='freshness-badge']");

        // Label asserts the human-readable text "3 days ago".
        Assert.Contains("3 days ago", badge.TextContent, StringComparison.OrdinalIgnoreCase);

        // CSS class confirms fresh styling (< 30 days).
        Assert.Equal("freshness-fresh", badge.GetAttribute("data-freshness-class"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // ADR-0026 § 4 load-bearing visual pin:
    //   > 90 days → "freshness-stale" → pale-amber background in app.css
    //
    // This test is DELIBERATELY LOAD-BEARING. Do not weaken to a structural
    // "badge rendered" check — the amber visual is the prospect-visible signal
    // that content freshness is tracked and surfaced in the UX.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_pale_amber_for_timestamps_older_than_90_days()
    {
        using var ctx = BuildCtx();

        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var ts  = now.AddDays(-120); // 120 days — past the 90-day stale threshold

        var cut = ctx.RenderComponent<FreshnessBadge>(p => p
            .Add(c => c.LastScrapedUtc, ts)
            .Add(c => c.Now, now));

        var badge = cut.Find("[data-testid='freshness-badge']");

        // The data-freshness-class attribute is the load-bearing pin.
        // "freshness-stale" maps to pale-amber border+color in app.css.
        Assert.Equal("freshness-stale", badge.GetAttribute("data-freshness-class"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Null timestamp: renders "freshness unknown" with neutral styling
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_neutral_for_null_timestamp()
    {
        using var ctx = BuildCtx();

        var cut = ctx.RenderComponent<FreshnessBadge>(p => p
            .Add(c => c.LastScrapedUtc, (DateTimeOffset?)null));

        var badge = cut.Find("[data-testid='freshness-badge']");

        Assert.Contains("freshness unknown", badge.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("freshness-neutral", badge.GetAttribute("data-freshness-class"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Middle range: 60 days renders neutral (not fresh, not stale)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_neutral_for_60_day_old_timestamp()
    {
        using var ctx = BuildCtx();

        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var ts  = now.AddDays(-60);

        var cut = ctx.RenderComponent<FreshnessBadge>(p => p
            .Add(c => c.LastScrapedUtc, ts)
            .Add(c => c.Now, now));

        var badge = cut.Find("[data-testid='freshness-badge']");
        Assert.Equal("freshness-neutral", badge.GetAttribute("data-freshness-class"));
    }
}
