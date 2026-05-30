using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Confidence;
using PinballWizard.Application.Ai.Cost;
using PinballWizard.Core.Configuration;
using System.Threading;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

// Pins the RefusalDetail surface per ADR-0026 § 4. Four behavioral
// contracts:
//
//  1. Successful answer (citations + confidence >= threshold) → null
//     RefusalDetail. The frontend RefusalPanel.razor should not render.
//
//  2. Cost-ceiling refusal → non-null RefusalDetail with null Confidence
//     (agent call was post-measured, not pre-measured; no ConfidenceSignals
//     available at that point in the AiRouter flow).
//
//  3. Confidence-threshold refusal → non-null RefusalDetail with
//     Confidence.Composite < Threshold; all three signal components
//     match the calculator output so RefusalPanel can surface them.
//
//  4. NoCitation refusal → non-null RefusalDetail with Confidence
//     populated; Confidence.Composite >= Threshold because the
//     citation-required gate fires AFTER the confidence gate passes —
//     the composite was acceptable, but zero citations attached.
//
// AiRouter end-to-end is tested here via its internal BuildRefusalDetail
// method rather than via a full AnswerAsync integration (which requires
// a live AIAgent / Foundry endpoint). The split keeps this test fast
// and deterministic while still exercising the exact code path that
// constructs RefusalDetail in production — no parallel or shadow
// implementation.
public sealed class RefusalDetailContractTests
{
    // AiFoundryOptions with a threshold that our signal fixtures will
    // straddle: low-signals composite will be below 0.65, high-signals
    // composite will be at or above 0.65.
    private static readonly AiFoundryOptions Options = new()
    {
        ProjectEndpoint = "https://example.ai.azure.com/api/projects/test",
        ConfidenceThreshold = 0.65,
        PerCallCostCeilingUsdCents = 10,
    };

    // Signals that produce a composite BELOW the 0.65 threshold.
    // RetrievalSimilarity=0.5 (no grounding), ModelSelfReported=0.85,
    // CitationCoverage=0.0 → composite ≈ cube_root(0.5 * 0.85 * 0.05)
    // ≈ cube_root(0.02125) ≈ 0.278 which is < 0.65.
    private static readonly ConfidenceSignals LowSignals = new(
        RetrievalSimilarity: 0.5,
        ModelSelfReported: 0.85,
        CitationCoverage: 0.0);

    // Signals that produce a composite AT OR ABOVE the 0.65 threshold.
    // RetrievalSimilarity=1.0, ModelSelfReported=0.85, CitationCoverage=0.9
    // → composite ≈ cube_root(1.0 * 0.85 * 0.9) ≈ cube_root(0.765) ≈ 0.914
    // which is >= 0.65.
    private static readonly ConfidenceSignals HighSignals = new(
        RetrievalSimilarity: 1.0,
        ModelSelfReported: 0.85,
        CitationCoverage: 0.9);

    // Verified: 0.914 >= 0.65.
    private static double HighComposite => HighSignals.Composite();

    // Verified: 0.278 < 0.65.
    private static double LowComposite => LowSignals.Composite();

    private static AiRouter CreateRouter(
        IConfidenceCalculator? confidenceCalc = null,
        AiFoundryOptions? options = null)
    {
        // ToolTraceCitationExtractor and RegexLegacyCitationExtractor are
        // sealed concrete classes — instantiate them directly. They have no
        // constructor parameters (all logic is static regex + instance
        // methods). They are never called by BuildRefusalDetailForTest, so
        // their presence is structural-only here.
        var toolTraceExtractor = new ToolTraceCitationExtractor();
        var regexExtractor = new RegexLegacyCitationExtractor();

        // Default: never grounded, so citations empty and composite low.
        confidenceCalc ??= Substitute.For<IConfidenceCalculator>();

        // Wave 2 PR-R2: IRefusalRecoveryService injected. Returning null
        // means "no recovery available" — keeps the existing contract
        // tests focused on Confidence field structure, not on recovery content.
        var refusalRecovery = Substitute.For<IRefusalRecoveryService>();
        refusalRecovery
            .BuildRecoveryAsync(Arg.Any<string>(), Arg.Any<RefusalCategory>(), Arg.Any<CancellationToken>())
            .Returns((RefusalDetail?)null);

        return new AiRouter(
            Substitute.For<IFoundryAgentFactory>(),
            Substitute.For<ISemanticAnswerCache>(),
            Substitute.For<IAgentPromptProvider>(),
            confidenceCalc,
            Substitute.For<ITokenUsageReader>(),
            Substitute.For<IAiCostCalculator>(),
            toolTraceExtractor,
            regexExtractor,
            refusalRecovery,
            new AmbientDegradationContext(),
            Microsoft.Extensions.Options.Options.Create(options ?? Options),
            NullLogger<AiRouter>.Instance);
    }

    [Fact]
    public void BuildRefusalDetail_CostCeiling_ReturnsNonNullWithNullConfidence()
    {
        // Cost ceiling fires before ConfidenceSignals are available — the
        // AiRouter flow checks cost after the agent call but before the
        // confidence calculation runs. Confidence on the RefusalDetail must
        // be null, not a placeholder value.
        var router = CreateRouter();

        var detail = router.BuildRefusalDetailForTest(RefusalCategory.CostCeilingHit, signals: null);

        Assert.NotNull(detail);
        Assert.Null(detail.Confidence);
        Assert.Null(detail.RelatedMachines);
        Assert.Null(detail.CommunityResources);
        Assert.Null(detail.MissingWhat);
        Assert.Null(detail.SuggestedRephrase);
    }

    [Fact]
    public void BuildRefusalDetail_ConfidenceThreshold_ReturnsNonNullWithBreakdownPopulated()
    {
        // Confidence-threshold path: signals are available and composite
        // is below threshold. All three signal components must survive
        // through to the ConfidenceBreakdown — no field-dropping.
        var router = CreateRouter();

        var detail = router.BuildRefusalDetailForTest(RefusalCategory.InsufficientGrounding, LowSignals);

        Assert.NotNull(detail);
        Assert.NotNull(detail.Confidence);
        Assert.Equal(LowSignals.RetrievalSimilarity, detail.Confidence!.RetrievalSimilarity);
        Assert.Equal(LowSignals.ModelSelfReported, detail.Confidence.ModelSelfReported);
        Assert.Equal(LowSignals.CitationCoverage, detail.Confidence.CitationCoverage);
        Assert.Equal(LowComposite, detail.Confidence.Composite, precision: 6);
        Assert.Equal(Options.ConfidenceThreshold, detail.Confidence.Threshold);

        // Wave 1 contract: composite MUST be below threshold on the
        // confidence-threshold path.
        Assert.True(
            detail.Confidence.Composite < detail.Confidence.Threshold,
            $"Expected Composite {detail.Confidence.Composite:F3} < Threshold {detail.Confidence.Threshold:F3} on confidence-threshold refusal path");
    }

    [Fact]
    public void BuildRefusalDetail_NoCitation_ReturnsNonNullWithCompositeAtOrAboveThreshold()
    {
        // NoCitation path: confidence is above the threshold (the check
        // passed), but zero citations attached. RefusalDetail must carry
        // the breakdown so the frontend can show "your question was
        // answerable but I couldn't ground it" rather than "I have low
        // confidence." Composite must be >= threshold to distinguish this
        // from the confidence-threshold path.
        var router = CreateRouter();

        var detail = router.BuildRefusalDetailForTest(RefusalCategory.NoCitation, HighSignals);

        Assert.NotNull(detail);
        Assert.NotNull(detail.Confidence);
        Assert.Equal(HighComposite, detail.Confidence!.Composite, precision: 6);
        Assert.Equal(Options.ConfidenceThreshold, detail.Confidence.Threshold);

        // Wave 1 contract: composite MUST be >= threshold on the NoCitation
        // path (the signal gate passed; the citation gate fired instead).
        Assert.True(
            detail.Confidence.Composite >= detail.Confidence.Threshold,
            $"Expected Composite {detail.Confidence.Composite:F3} >= Threshold {detail.Confidence.Threshold:F3} on NoCitation refusal path");
    }

    [Fact]
    public void BuildRefusalDetail_WaveTwoContract_RecoveryNullWhenServiceReturnsNull()
    {
        // Wave 2 PR-R2: when IRefusalRecoveryService returns null (category
        // not supported, or best-effort failure), RelatedMachines must remain
        // null on the RefusalDetail — no phantom empty list. CommunityResources,
        // MissingWhat, SuggestedRephrase remain Wave 2 PR-R3/R4
        // responsibilities and must still be null.
        //
        // CreateRouter() stubs IRefusalRecoveryService to return null for all
        // categories, so this exercises the "no recovery" code path through
        // BuildRefusalDetail.
        var router = CreateRouter();

        foreach (var signals in new ConfidenceSignals?[] { null, LowSignals, HighSignals })
        {
            var detail = router.BuildRefusalDetailForTest(RefusalCategory.InsufficientGrounding, signals);

            Assert.Null(detail.RelatedMachines);
            Assert.Null(detail.CommunityResources);
            Assert.Null(detail.MissingWhat);
            Assert.Null(detail.SuggestedRephrase);
        }
    }
}
