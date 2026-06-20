using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Citations;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Citations;

// Behavioral tests for CitationGroup.
//
// Per modern-lcd.md "Many-citations behavior" and FE-09 (citation-as-hero),
// all citations are rendered full-fidelity with no disclosure toggle.
// Tests assert: all cards present, no disclosure elements, correct sort order.
public sealed class CitationGroupTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static Citation MakeCitation(string title, double? score = null) =>
        new(title,
            SourceUrl: $"https://example.com/{title.Replace(' ', '-').ToLowerInvariant()}",
            RelevanceScore: score);

    private static BunitContext BuildCtx()
    {
        var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // All citations render as cards — no disclosure, no collapse
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task All_citations_render_as_cards_with_no_disclosure_button()
    {
        await using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("Low Score Doc",  score: 0.40),
            MakeCitation("High Score Doc", score: 0.92),
            MakeCitation("Mid Score Doc",  score: 0.65),
        };

        var cut = ctx.Render<CitationGroup>(p => p
            .Add(c => c.Host, "example.com")
            .Add(c => c.Citations, citations));

        // All three citation titles must be present in the markup.
        Assert.Contains("High Score Doc", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mid Score Doc",  cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Low Score Doc",  cut.Markup, StringComparison.OrdinalIgnoreCase);

        // No disclosure elements exist — not the expand button, not the collapse button.
        Assert.Empty(cut.FindAll("[data-testid='citation-group-expand-button']"));
        Assert.Empty(cut.FindAll("[data-testid='citation-group-collapse-button']"));
        Assert.Empty(cut.FindAll("[data-testid='citation-group-disclosure']"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Single-citation group: the one card renders, no disclosure
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Group_with_single_citation_renders_the_card_without_disclosure()
    {
        await using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("Only Doc", score: 0.80),
        };

        var cut = ctx.Render<CitationGroup>(p => p
            .Add(c => c.Host, "example.com")
            .Add(c => c.Citations, citations));

        Assert.Contains("Only Doc", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-testid='citation-group-expand-button']"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // SortedCitations orders by RelevanceScore descending (nulls last)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SortedCitations_returns_citations_ordered_by_score_descending()
    {
        var citations = new List<Citation>
        {
            MakeCitation("Low",  score: 0.30),
            MakeCitation("High", score: 0.90),
            MakeCitation("Mid",  score: 0.60),
            MakeCitation("Null", score: null),
        };

        var sorted = CitationGroup.SortedCitations(citations);

        Assert.Equal("High", sorted[0].Title);
        Assert.Equal("Mid",  sorted[1].Title);
        Assert.Equal("Low",  sorted[2].Title);
        Assert.Equal("Null", sorted[3].Title);
    }
}
