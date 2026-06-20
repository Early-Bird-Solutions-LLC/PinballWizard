using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Citations;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Citations;

// Behavioral tests for CitationStrip.
//
// Tests assert: group ordering (highest-max-score first), graceful empty
// state (renders nothing), grouping logic (same-host citations share a group),
// and the summary header format "SOURCES · N cited from M sites" with correct
// singular/plural forms.
public sealed class CitationStripTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static Citation MakeCitation(
        string host,
        string title,
        double? score = null,
        string? path = null) =>
        new(title,
            SourceUrl: $"https://{host}/{path ?? title.Replace(' ', '-').ToLowerInvariant()}",
            RelevanceScore: score);

    private static BunitContext BuildCtx()
    {
        var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Groups are sorted by max RelevanceScore descending
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Renders_groups_sorted_by_max_relevance_score_desc()
    {
        await using var ctx = BuildCtx();

        // Two host groups: sternpinball.com has a lower max score,
        // opdb.org has a higher max score — opdb.org group should render first.
        var citations = new List<Citation>
        {
            MakeCitation("sternpinball.com", "Stern Manual",   score: 0.55),
            MakeCitation("opdb.org",          "OPDB Record A", score: 0.91),
            MakeCitation("opdb.org",          "OPDB Record B", score: 0.72),
        };

        var cut = ctx.Render<CitationStrip>(p => p
            .Add(c => c.Citations, citations));

        // Both groups must render.
        var groups = cut.FindAll("[data-testid='citation-group']");
        Assert.Equal(2, groups.Count);

        // The group rendered first (index 0) should be opdb.org (max score 0.91 > 0.55).
        Assert.Equal("opdb.org",         groups.ElementAt(0).GetAttribute("data-host"));
        Assert.Equal("sternpinball.com", groups.ElementAt(1).GetAttribute("data-host"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Empty citations list: renders nothing (no strip element)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Renders_no_groups_when_citations_list_is_empty()
    {
        await using var ctx = BuildCtx();

        var cut = ctx.Render<CitationStrip>(p => p
            .Add(c => c.Citations, Array.Empty<Citation>()));

        // When Citations is empty the strip div is not rendered.
        var strip = cut.FindAll("[data-testid='citation-strip']");
        Assert.Empty(strip);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Multiple citations from same host → grouped together
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Citations_from_same_host_are_grouped()
    {
        await using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("sternpinball.com", "Manual A", score: 0.90, path: "manual-a"),
            MakeCitation("sternpinball.com", "Manual B", score: 0.70, path: "manual-b"),
        };

        var cut = ctx.Render<CitationStrip>(p => p
            .Add(c => c.Citations, citations));

        // Both citations from the same host → one group.
        var groups = cut.FindAll("[data-testid='citation-group']");
        Assert.Single(groups);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Summary header — many citations from multiple hosts
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_header_shows_correct_count_and_site_count_for_multiple()
    {
        await using var ctx = BuildCtx();

        // 5 citations across 3 hosts → "SOURCES · 5 cited from 3 sites"
        var citations = new List<Citation>
        {
            MakeCitation("sternpinball.com", "Stern A",  score: 0.80, path: "a"),
            MakeCitation("sternpinball.com", "Stern B",  score: 0.70, path: "b"),
            MakeCitation("opdb.org",          "OPDB A",  score: 0.91, path: "a"),
            MakeCitation("opdb.org",          "OPDB B",  score: 0.60, path: "b"),
            MakeCitation("pinballbrothers.com","PB A",   score: 0.55, path: "a"),
        };

        var cut = ctx.Render<CitationStrip>(p => p
            .Add(c => c.Citations, citations));

        Assert.Contains("5 cited from 3 sites", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Summary header — singular: 1 citation from 1 host
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_header_uses_singular_form_for_one_citation_one_site()
    {
        await using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("sternpinball.com", "Stern Manual", score: 0.80),
        };

        var cut = ctx.Render<CitationStrip>(p => p
            .Add(c => c.Citations, citations));

        Assert.Contains("1 cited from 1 site", cut.Markup, StringComparison.OrdinalIgnoreCase);
        // Confirm "sites" (plural) is NOT present — guard the singular form.
        Assert.DoesNotContain("1 sites", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Summary header — "SOURCES ·" prefix always present
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_header_always_contains_SOURCES_prefix()
    {
        await using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("opdb.org", "OPDB Record", score: 0.75),
        };

        var cut = ctx.Render<CitationStrip>(p => p
            .Add(c => c.Citations, citations));

        Assert.Contains("SOURCES", cut.Markup, StringComparison.OrdinalIgnoreCase);
        // The strip element itself must still carry its aria-label.
        var strip = cut.Find("[data-testid='citation-strip']");
        Assert.NotNull(strip);
        Assert.Equal("Sources", strip.GetAttribute("aria-label"));
    }
}
