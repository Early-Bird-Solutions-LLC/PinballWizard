using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Retrieval;
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
//
// Flipper-button tests (design-system gap #2):
//   - Right flipper always present; asserts href, rel, target, and label text.
//   - Left flipper absent when InAnswerAnchor is null; present with correct
//     href="#<anchor>" when set (wired by gap #3 — inline citation markers).
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
        double? relevanceScore = null,
        string? documentChunkId = null) =>
        new(title, sourceUrl, PageStart: pageStart, PageEnd: pageEnd,
            SectionHeading: sectionHeading, SourceType: sourceType,
            LastScrapedUtc: lastScrapedUtc, RelevanceScore: relevanceScore,
            DocumentChunkId: documentChunkId);

    private static BunitContext BuildCtx()
    {
        var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Page anchor: single-page (PageStart=42, PageEnd=null)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Page_anchor_renders_p_42_for_single_page()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(pageStart: 42, pageEnd: null);

        var cut = ctx.Render<CitationCard>(p => p
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
    public async Task Page_anchor_renders_p_42_to_47_for_range()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(pageStart: 42, pageEnd: 47);

        var cut = ctx.Render<CitationCard>(p => p
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
    public async Task Page_anchor_absent_when_PageStart_is_null()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(pageStart: null, pageEnd: null);

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var anchors = cut.FindAll("[data-testid='citation-page-anchor']");
        Assert.Empty(anchors);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Section heading: renders when present, absent when null
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Section_heading_renders_when_present()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(sectionHeading: "Wizard Mode Rules");

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var heading = cut.Find("[data-testid='citation-section-heading']");
        Assert.Contains("Wizard Mode Rules", heading.TextContent);
    }

    [Fact]
    public async Task Section_heading_absent_when_null()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(sectionHeading: null);

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var headings = cut.FindAll("[data-testid='citation-section-heading']");
        Assert.Empty(headings);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Right flipper (VIEW THE ORIGINAL ▶) — security pin + label + href.
    //
    // Replaces the old ↗ link test. The data-testid="citation-source-link"
    // selector is preserved so existing E2E/integration selectors continue
    // to work unchanged (design-system gap #2).
    //
    // Load-bearing per feedback_community_resource_posture.md and
    // PR self-audit item 9(e). If the rel assertion fails, opener isolation
    // or referrer suppression is missing — security 🔴.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Right_flipper_has_target_blank_and_rel_noopener_noreferrer() // non-corpus path; corpus-chunk security baseline covered by CorpusChunkCitation_SecondaryFileLink_GoesToSourceUrl
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(sourceUrl: "https://sternpinball.com/manuals/test.pdf");

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var link = cut.Find("[data-testid='citation-source-link']");

        Assert.Equal("_blank", link.GetAttribute("target"));

        var rel = link.GetAttribute("rel") ?? string.Empty;
        Assert.Contains("noopener", rel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("noreferrer", rel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Right_flipper_href_matches_citation_source_url()
    {
        const string url = "https://sternpinball.com/manuals/test.pdf";
        await using var ctx = BuildCtx();
        var citation = BuildCitation(sourceUrl: url);

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var link = cut.Find("[data-testid='citation-source-link']");
        Assert.Equal(url, link.GetAttribute("href"));
    }

    [Fact]
    public async Task Right_flipper_label_contains_VIEW_THE_ORIGINAL()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation();

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var link = cut.Find("[data-testid='citation-source-link']");
        Assert.Contains("VIEW THE ORIGINAL", link.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Left flipper (◀ VIEW IN ANSWER) — controlled by HasInlineMarker + Ordinal.
    //
    // InAnswerAnchor is now a computed property (not a parameter): it equals
    // "marker-{Ordinal}-1" when HasInlineMarker=true, null otherwise.
    // Until Task 6 wires the body markers, HasInlineMarker defaults to false
    // so the left flipper stays hidden.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Left_flipper_is_absent_when_HasInlineMarker_is_false()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation();

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation)
            .Add(c => c.Ordinal, 3)
            .Add(c => c.HasInlineMarker, false));
        // HasInlineMarker=false → InAnswerAnchor=null → left flipper must not appear.

        var leftFlippers = cut.FindAll("[data-testid='citation-flipper-in-answer']");
        Assert.Empty(leftFlippers);
    }

    [Fact]
    public async Task Left_flipper_is_present_with_correct_href_when_HasInlineMarker_is_true()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation();

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation)
            .Add(c => c.Ordinal, 3)
            .Add(c => c.HasInlineMarker, true));

        var leftFlipper = cut.Find("[data-testid='citation-flipper-in-answer']");
        // InAnswerAnchor = "marker-3-1" → href = "#marker-3-1"
        Assert.Equal("#marker-3-1", leftFlipper.GetAttribute("href"));
    }

    [Fact]
    public async Task Left_flipper_label_contains_VIEW_IN_ANSWER()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation();

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation)
            .Add(c => c.Ordinal, 3)
            .Add(c => c.HasInlineMarker, true));

        var leftFlipper = cut.Find("[data-testid='citation-flipper-in-answer']");
        Assert.Contains("VIEW IN ANSWER", leftFlipper.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Ordinal: DOM id + visible badge (Task 4)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Card_root_has_citation_N_id_matching_ordinal()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation();

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation)
            .Add(c => c.Ordinal, 5));

        var card = cut.Find("[data-testid='citation-card']");
        Assert.Equal("citation-5", card.Id);
    }

    [Fact]
    public async Task Ordinal_badge_renders_visible_number()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation();

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation)
            .Add(c => c.Ordinal, 7));

        var badge = cut.Find("[data-testid='citation-ordinal']");
        Assert.Equal("7", badge.TextContent.Trim());
    }

    // ──────────────────────────────────────────────────────────────────────
    // High-score accent: data-relevance-score attribute matches input
    // ──────────────────────────────────────────────────────────────────────

    // RelevanceScore is the Azure semantic reranker score (0–4 range), NOT a
    // 0–1 fraction. It must render as a normalized 0–100% match — the old
    // `score * 100` produced nonsense like "190% match" (the value seen live).
    [Theory]
    [InlineData(1.9, "48% match")]    // a typical reranker score — was "190% match"
    [InlineData(2.47, "62% match")]   // the value seen live ("247% match")
    [InlineData(3.4, "85% match")]    // at the high-score bar
    [InlineData(4.0, "100% match")]   // reranker ceiling
    [InlineData(8.0, "100% match")]   // BM25-fallback above the ceiling clamps to 100, never exceeds
    public async Task Relevance_score_renders_as_normalized_percent(double rerankerScore, string expected)
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(relevanceScore: rerankerScore);

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var scoreEl = cut.Find("[data-testid='citation-relevance-score']");
        Assert.Equal(expected, scoreEl.TextContent.Trim());
    }

    [Fact]
    public async Task Strong_match_gets_high_score_accent()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(relevanceScore: 3.4); // 85% — at HighMatchPercent

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var card = cut.Find("[data-testid='citation-card']");
        Assert.Contains("citation-card-high-score", card.GetAttribute("class"));
    }

    [Fact]
    public async Task Moderate_match_does_not_get_high_score_accent()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(relevanceScore: 1.9); // 48% — below the bar

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var card = cut.Find("[data-testid='citation-card']");
        Assert.DoesNotContain("citation-card-high-score", card.GetAttribute("class"));
    }

    [Fact]
    public async Task Null_relevance_score_does_not_render_score_element()
    {
        await using var ctx = BuildCtx();
        var citation = BuildCitation(relevanceScore: null);

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var scoreEls = cut.FindAll("[data-testid='citation-relevance-score']");
        Assert.Empty(scoreEls);
    }

    // ──────────────────────────────────────────────────────────────────────
    // DocumentChunkId deep-links: corpus-chunk citations navigate internally
    // to /documents/{id}; non-corpus citations keep the original external link.
    //
    // When DocumentChunkId is set:
    //   - Primary link (data-testid="citation-source-link") → /documents/{id},
    //     no target/_blank, no rel (internal Blazor navigation).
    //   - Secondary link (data-testid="citation-file-link") → SourceUrl, new tab.
    // When DocumentChunkId is null:
    //   - Single primary link → SourceUrl, target="_blank", rel=noopener noreferrer.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CorpusChunkCitation_PrimaryLink_GoesToDocumentPage()
    {
        const string chunkId = "doc_abc123def456";
        await using var ctx = BuildCtx();
        var citation = BuildCitation(documentChunkId: chunkId);

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var link = cut.Find("[data-testid='citation-source-link']");
        Assert.Equal($"/documents/{chunkId}", link.GetAttribute("href"));
        // Internal Blazor navigation — must not open a new tab.
        Assert.Null(link.GetAttribute("target"));
        Assert.Null(link.GetAttribute("rel"));
    }

    [Fact]
    public async Task CorpusChunkCitation_SecondaryFileLink_GoesToSourceUrl()
    {
        const string sourceUrl = "https://sternpinball.com/manuals/test.pdf";
        await using var ctx = BuildCtx();
        var citation = BuildCitation(
            sourceUrl: sourceUrl,
            documentChunkId: "doc_abc123def456");

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var fileLink = cut.Find("[data-testid='citation-file-link']");
        Assert.Equal(sourceUrl, fileLink.GetAttribute("href"));
        // External link — must carry security attributes.
        Assert.Equal("_blank", fileLink.GetAttribute("target"));
        var rel = fileLink.GetAttribute("rel") ?? string.Empty;
        Assert.Contains("noopener", rel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("noreferrer", rel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonCorpusCitation_PrimaryLink_GoesToSourceUrl()
    {
        const string sourceUrl = "https://sternpinball.com/manuals/test.pdf";
        await using var ctx = BuildCtx();
        // documentChunkId defaults to null → non-corpus path.
        var citation = BuildCitation(sourceUrl: sourceUrl);

        var cut = ctx.Render<CitationCard>(p => p
            .Add(c => c.Citation, citation));

        var link = cut.Find("[data-testid='citation-source-link']");
        Assert.Equal(sourceUrl, link.GetAttribute("href"));
        // Non-corpus primary link is external — security attributes required.
        Assert.Equal("_blank", link.GetAttribute("target"));
        var rel = link.GetAttribute("rel") ?? string.Empty;
        Assert.Contains("noopener", rel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("noreferrer", rel, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Cross-layer parity: CitationCard badge vs shared RetrievalScoring helper
    // ──────────────────────────────────────────────────────────────────────

    // Cross-layer contract test (not a behavioral one): pins that the badge's
    // MatchPercent still DELEGATES to the shared RetrievalScoring helper. It
    // catches constant-drift (if MaxRerankerScore changes) and clamp-removal
    // (the 8.0 case), and fails the build if MatchPercent stops using the
    // shared normalizer — the structural guarantee against the 0-4-vs-0-1
    // scale bug re-emerging. It would NOT catch re-inlining an algebraically
    // identical formula; that regression is prevented by the code, not this test.
    [Theory]
    [InlineData(1.12)]  // the 28% Cactus Canyon card
    [InlineData(1.6)]
    [InlineData(3.4)]
    [InlineData(4.0)]
    [InlineData(8.0)]
    public void MatchPercent_equals_shared_normalization(double rerankerScore)
    {
        var expected = (int)Math.Round(RetrievalScoring.NormalizeRerankerScore(rerankerScore) * 100.0);
        Assert.Equal(expected, CitationCard.MatchPercent(rerankerScore));
    }
}
