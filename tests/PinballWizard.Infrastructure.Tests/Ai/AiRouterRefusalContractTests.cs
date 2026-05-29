using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

// Pins the contract that the IAiRouter refusal surface presents to
// callers + dashboards. Two layers:
//
//  - Enum stability: every dashboard query / alert rule that filters by
//    `refusal_category` value depends on the ToString() representation
//    of the enum. A rename or reorder is a downstream-breaking change
//    that must surface in this test before it surfaces in a silent
//    dashboard zero.
//
//  - Per-category user-facing text: the refusal message is the user's
//    only signal that the system declined to answer. A silent rewrite
//    would change UX without firing any behavior test elsewhere — this
//    is the local guard. Each category's text must (a) be non-empty,
//    (b) lead with "I don't know" so the refusal posture per
//    vision.md "refuse rather than fabricate" is visually consistent
//    across categories, and (c) be unique per category so an operator
//    can identify which gate fired from the user-visible surface alone.
//
// AiRouter end-to-end behavior (the full citations.Count == 0 →
// NoCitation refusal flow per ADR-0023) is exercised via the H3 eval
// baseline (build-spec § Phase 4 scope item 24), not a unit test —
// the surrounding AgentResponse / IFoundryAgentFactory contract is
// stubborn enough to mock that the cost-vs-coverage trade lands on
// the integration side. This file pins the seams only.
public sealed class AiRouterRefusalContractTests
{
    [Fact]
    public void RefusalCategory_NoCitation_IsValueFive()
    {
        // Pinned numeric value because telemetry consumers historically
        // serialize enum-as-int when minimizing payloads. A reorder
        // (e.g., inserting a new category before NoCitation) would
        // shift the value silently — caught here.
        Assert.Equal(5, (int)RefusalCategory.NoCitation);
    }

    [Theory]
    [InlineData(RefusalCategory.InsufficientGrounding)]
    [InlineData(RefusalCategory.OutOfScope)]
    [InlineData(RefusalCategory.LowModelConfidence)]
    [InlineData(RefusalCategory.CostCeilingHit)]
    [InlineData(RefusalCategory.HarmfulContent)]
    [InlineData(RefusalCategory.NoCitation)]
    [InlineData(RefusalCategory.UpstreamThrottled)]
    public void BuildRefusalText_EveryCategory_ReturnsNonEmptyAndStartsWithIDontKnow(RefusalCategory category)
    {
        var text = AiRouter.BuildRefusalText(category);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.StartsWith("I don't know", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRefusalText_EveryCategoryEmitsDistinctText()
    {
        // Distinct per-category text is the contract that lets operators
        // identify which gate fired from the user-visible answer alone
        // without cross-referencing telemetry. Distinctness must hold
        // across all 7 categories — no two categories may share a
        // refusal message.
        var allCategories = new[]
        {
            RefusalCategory.InsufficientGrounding,
            RefusalCategory.OutOfScope,
            RefusalCategory.LowModelConfidence,
            RefusalCategory.CostCeilingHit,
            RefusalCategory.HarmfulContent,
            RefusalCategory.NoCitation,
            RefusalCategory.UpstreamThrottled,
        };

        var texts = allCategories
            .Select(AiRouter.BuildRefusalText)
            .ToArray();

        Assert.Equal(allCategories.Length, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void BuildRefusalText_NoCitation_MentionsCorpusOrCitation()
    {
        // The NoCitation category specifically signals "the answer
        // wasn't grounded in a source." The user-facing text must
        // surface that distinction (vs. the more generic
        // InsufficientGrounding) so a user understands "ask a more
        // specific question / ask about a covered machine" rather
        // than "the system is having a bad day". Substring assertion
        // keeps the test loose to copy edits while pinning the
        // user-recoverability framing.
        var text = AiRouter.BuildRefusalText(RefusalCategory.NoCitation);

        Assert.True(
            text.Contains("cite", StringComparison.OrdinalIgnoreCase)
            || text.Contains("citation", StringComparison.OrdinalIgnoreCase)
            || text.Contains("source", StringComparison.OrdinalIgnoreCase)
            || text.Contains("corpus", StringComparison.OrdinalIgnoreCase),
            $"NoCitation refusal should mention citation/source/corpus framing; got: {text}");
    }
}
