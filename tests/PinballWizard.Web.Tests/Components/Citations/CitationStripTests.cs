using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Citations;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Citations;

// Behavioral tests for CitationStrip.
//
// Tests assert group ordering (highest-max-score group first), graceful empty
// state (renders nothing), and single-citation groups (no disclosure button).
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
        Assert.Equal("opdb.org", groups.ElementAt(0).GetAttribute("data-host"));
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
    // Single citation per group: no disclosure button rendered
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Group_with_single_citation_does_not_show_disclosure()
    {
        await using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("sternpinball.com", "Stern Manual", score: 0.80),
        };

        var cut = ctx.Render<CitationStrip>(p => p
            .Add(c => c.Citations, citations));

        // One group renders.
        var groups = cut.FindAll("[data-testid='citation-group']");
        Assert.Single(groups);

        // No expand button — only one citation in the group.
        var expandBtns = cut.FindAll("[data-testid='citation-group-expand-button']");
        Assert.Empty(expandBtns);
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

        // Two citations in the group means the expand button appears.
        var expandBtns = cut.FindAll("[data-testid='citation-group-expand-button']");
        Assert.Single(expandBtns);
    }
}
