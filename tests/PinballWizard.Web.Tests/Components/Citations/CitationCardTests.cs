using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Citations;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Citations;

// Behavioral tests for CitationCard.
//
// Tests assert rendered DOM behavior — page anchor text format, section heading
// conditional visibility, and the security-baseline rel="noopener noreferrer" on
// external links. Structural markup (MudPaper internals, CSS class names) is NOT
// asserted to avoid brittleness against MudBlazor version updates.
//
// The external-link security test (rel=noopener noreferrer) is a load-bearing pin
// per feedback_community_resource_posture.md and the PR self-audit item 9(e).
public sealed class CitationCardTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Helper: build a minimal Citation for testing
    // ──────────────────────────────────────────────────────────────────────

    private static Citation BuildCitation(
        string title = "Test Machine Manual",
        string sourceUrl = "https://sternpinball.com/manuals/test.pdf",
        int? pageStart = null,
        int? pageEnd = null,
        string? sectionHeading = null,
        CitationSourceType sourceType = CitationSourceType.CorpusChunk,
        DateTimeOffset? lastScrapedUtc = null,
        double? relevanceScore = null) =>
        new(title, sourceUrl, PageStart: pageStart, PageEnd: pageEnd,
            SectionHeading: sectionHeading, SourceType: sourceType,
            LastScrapedUtc: lastScrapedUtc, RelevanceScore: relevanceScore);

    private static TestContext BuildCtx()
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Page anchor: single-page (PageStart=42, PageEnd=null)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Page_anchor_renders_p_42_for_single_page()
    {
        using var ctx = BuildCtx();
        var citation = BuildCitation(pageStart: 42, pageEnd: null);

        var cut = ctx.RenderComponent<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var anchor = cut.Find("[data-testid='citation-page-anchor']");
        Assert.Contains("42", anchor.TextContent);
        // Should not contain a range dash.
        Assert.DoesNotContain("–", anchor.TextContent);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Page anchor: range (PageStart=42, PageEnd=47)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Page_anchor_renders_p_42_to_47_for_range()
    {
        using var ctx = BuildCtx();
        var citation = BuildCitation(pageStart: 42, pageEnd: 47);

        var cut = ctx.RenderComponent<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var anchor = cut.Find("[data-testid='citation-page-anchor']");
        var text = anchor.TextContent;
        Assert.Contains("42", text);
        Assert.Contains("47", text);
        // En-dash separates start and end page in the range.
        Assert.Contains("–", text);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Page anchor: absent when PageStart is null
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Page_anchor_absent_when_PageStart_is_null()
    {
        using var ctx = BuildCtx();
        var citation = BuildCitation(pageStart: null, pageEnd: null);

        var cut = ctx.RenderComponent<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var anchors = cut.FindAll("[data-testid='citation-page-anchor']");
        Assert.Empty(anchors);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Section heading: renders when present, absent when null
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Section_heading_renders_when_present()
    {
        using var ctx = BuildCtx();
        var citation = BuildCitation(sectionHeading: "Wizard Mode Rules");

        var cut = ctx.RenderComponent<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var heading = cut.Find("[data-testid='citation-section-heading']");
        Assert.Contains("Wizard Mode Rules", heading.TextContent);
    }

    [Fact]
    public void Section_heading_absent_when_null()
    {
        using var ctx = BuildCtx();
        var citation = BuildCitation(sectionHeading: null);

        var cut = ctx.RenderComponent<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var headings = cut.FindAll("[data-testid='citation-section-heading']");
        Assert.Empty(headings);
    }

    // ──────────────────────────────────────────────────────────────────────
    // External link security pin: rel="noopener noreferrer" + target="_blank"
    //
    // Load-bearing per feedback_community_resource_posture.md and
    // PR self-audit item 9(e). If this test fails, the link is missing
    // the opener isolation or referrer suppression — security 🔴.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void External_link_has_target_blank_and_rel_noopener_noreferrer()
    {
        using var ctx = BuildCtx();
        var citation = BuildCitation(sourceUrl: "https://sternpinball.com/manuals/test.pdf");

        var cut = ctx.RenderComponent<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var link = cut.Find("[data-testid='citation-source-link']");

        Assert.Equal("_blank", link.GetAttribute("target"));

        var rel = link.GetAttribute("rel") ?? string.Empty;
        Assert.Contains("noopener", rel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("noreferrer", rel, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // High-score accent: data-relevance-score attribute matches input
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void High_score_citation_shows_relevance_score_text()
    {
        using var ctx = BuildCtx();
        var citation = BuildCitation(relevanceScore: 0.92);

        var cut = ctx.RenderComponent<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var scoreEl = cut.Find("[data-testid='citation-relevance-score']");
        Assert.Contains("92", scoreEl.TextContent); // "92% match"
    }

    [Fact]
    public void Null_relevance_score_does_not_render_score_element()
    {
        using var ctx = BuildCtx();
        var citation = BuildCitation(relevanceScore: null);

        var cut = ctx.RenderComponent<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var scoreEls = cut.FindAll("[data-testid='citation-relevance-score']");
        Assert.Empty(scoreEls);
    }
}
