using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Citations;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Citations;

// Behavioral tests for CitationGroup.
//
// Tests assert that the highest-scoring citation is always visible, that
// lower-scoring citations are collapsed behind the disclosure button, and that
// a single-citation group does not show the disclosure at all.
public sealed class CitationGroupTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static Citation MakeCitation(string title, double? score = null) =>
        new(title,
            SourceUrl: $"https://example.com/{title.Replace(' ', '-').ToLowerInvariant()}",
            RelevanceScore: score);

    private static TestContext BuildCtx()
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // The highest-scoring citation is visible by default (not collapsed)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Highest_scoring_citation_in_group_visible_by_default()
    {
        using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("Low Score Doc",  score: 0.40),
            MakeCitation("High Score Doc", score: 0.92),
            MakeCitation("Mid Score Doc",  score: 0.65),
        };

        var cut = ctx.RenderComponent<CitationGroup>(p => p
            .Add(c => c.Host, "example.com")
            .Add(c => c.Citations, citations));

        // The primary (highest-score) card is always rendered outside the disclosure.
        // Verify "High Score Doc" title appears in the initial markup.
        Assert.Contains("High Score Doc", cut.Markup, StringComparison.OrdinalIgnoreCase);

        // The expand button is visible (there are 2 more behind it).
        var expandBtn = cut.Find("[data-testid='citation-group-expand-button']");
        Assert.NotNull(expandBtn);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Lower-scoring citations are collapsed behind the disclosure
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lower_scoring_citations_collapsed_into_more_disclosure()
    {
        using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("Low Score Doc",  score: 0.40),
            MakeCitation("High Score Doc", score: 0.92),
        };

        var cut = ctx.RenderComponent<CitationGroup>(p => p
            .Add(c => c.Host, "example.com")
            .Add(c => c.Citations, citations));

        // Before expanding: disclosure button present, "Low Score Doc" not visible.
        var expandBtn = cut.Find("[data-testid='citation-group-expand-button']");
        Assert.NotNull(expandBtn);

        // Click to expand.
        await cut.InvokeAsync(() => expandBtn.Click());

        // After expanding: both citations visible (collapse button present).
        Assert.Contains("Low Score Doc", cut.Markup, StringComparison.OrdinalIgnoreCase);
        var collapseBtn = cut.Find("[data-testid='citation-group-collapse-button']");
        Assert.NotNull(collapseBtn);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Single-citation group has no disclosure button
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Group_with_single_citation_does_not_show_disclosure()
    {
        using var ctx = BuildCtx();

        var citations = new List<Citation>
        {
            MakeCitation("Only Doc", score: 0.80),
        };

        var cut = ctx.RenderComponent<CitationGroup>(p => p
            .Add(c => c.Host, "example.com")
            .Add(c => c.Citations, citations));

        // No expand button when there is only one citation.
        var expandBtns = cut.FindAll("[data-testid='citation-group-expand-button']");
        Assert.Empty(expandBtns);

        // The single citation is rendered directly.
        Assert.Contains("Only Doc", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
