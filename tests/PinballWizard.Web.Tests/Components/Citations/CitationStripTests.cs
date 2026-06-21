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
    // Cards receive sequential citation-{N} ids across groups (Task 4)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cards_get_sequential_ordinals_and_anchor_ids_across_groups()
    {
        await using var ctx = BuildCtx();

        // Three citations: A and C share a host (a.com), B is on b.com.
        // Ordering: B has highest score → rendered first (ordinal 1).
        // A has next highest → ordinal 2 (first in a.com group).
        // C has lowest → ordinal 3 (second in a.com group).
        var citations = new List<Citation>
        {
            new("A", "https://a.com/1", RelevanceScore: 0.9),
            new("B", "https://b.com/1", RelevanceScore: 0.95),
            new("C", "https://a.com/2", RelevanceScore: 0.7),
        };

        var cut = ctx.Render<CitationStrip>(p => p
            .Add(x => x.Citations, citations));

        var cards = cut.FindAll("[data-testid='citation-card']");
        Assert.Equal(3, cards.Count);

        // Each card root must have a sequential citation-{N} id in render order.
        var ids = cards.Select(c => c.Id).ToList();
        string[] expectedIds = ["citation-1", "citation-2", "citation-3"];
        Assert.Equal(expectedIds, ids);
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

    // ──────────────────────────────────────────────────────────────────────
    // Task 8: Left flipper shows only for cards whose ordinal appears in body
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Left_flipper_shows_only_for_cards_referenced_in_the_body()
    {
        await using var ctx = BuildCtx();

        var citations = new[] { new Citation("A", "https://a.com/1", RelevanceScore: 0.9),
                                new Citation("B", "https://b.com/1", RelevanceScore: 0.8) };
        // Body cites only ordinal 1.
        var cut = ctx.Render<CitationStrip>(p => p
            .Add(x => x.Citations, citations)
            .Add(x => x.AnswerBody, "Grounded claim [[cite:1]]."));
        var inAnswer = cut.FindAll("[data-testid='citation-flipper-in-answer']");
        Assert.Single(inAnswer); // only card 1 lights its left flipper
    }

    // ──────────────────────────────────────────────────────────────────────
    // Task 8: CitationOrdering.InRenderOrder produces same order as card ordinals
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void InRenderOrder_matches_card_ordinal_order()
    {
        // Three citations: B has highest score → first in render order (card N=1).
        // A and C share a.com; A has higher score → A is N=2, C is N=3.
        var citations = new List<Citation>
        {
            new("A", "https://a.com/1", RelevanceScore: 0.9),
            new("B", "https://b.com/1", RelevanceScore: 0.95),
            new("C", "https://a.com/2", RelevanceScore: 0.7),
        };

        var ordered = CitationOrdering.InRenderOrder(citations);

        // InRenderOrder must match the card render order: B first, then A, then C.
        Assert.Equal(3, ordered.Count);
        Assert.Equal("B", ordered[0].Title);
        Assert.Equal("A", ordered[1].Title);
        Assert.Equal("C", ordered[2].Title);
    }
}
