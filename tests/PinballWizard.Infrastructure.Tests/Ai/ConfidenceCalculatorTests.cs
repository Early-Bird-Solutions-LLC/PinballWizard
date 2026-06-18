using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Confidence;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

public sealed class ConfidenceCalculatorTests
{
    private static readonly Citation OpdbCitation = new(
        Title: "OPDB record GRBN-MQR4P",
        SourceUrl: "https://opdb.org/machines/GRBN-MQR4P",
        MachineId: "GRBN-MQR4P",
        DocumentChunkId: null);

    [Fact]
    public void Compute_WithCitations_HighSignals()
    {
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute(
            "Foo Fighters is a Stern Pinball machine from 2023. https://opdb.org/machines/GRBN-MQR4P",
            [OpdbCitation]);

        Assert.Equal(1.0, signals.RetrievalSimilarity);
        Assert.True(signals.CitationCoverage > 0.5);
        // Composite should pass the default 0.65 threshold.
        Assert.True(signals.Composite() >= 0.65);
    }

    [Fact]
    public void Compute_NoCitations_LowSignals()
    {
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute("I think Foo Fighters is by some manufacturer.", []);

        Assert.Equal(0.5, signals.RetrievalSimilarity);
        Assert.Equal(0.0, signals.CitationCoverage);
        // Composite should NOT pass the default 0.65 threshold.
        Assert.True(signals.Composite() < 0.65);
    }

    [Fact]
    public void Compute_NullCitations_Throws()
    {
        var calc = new ConfidenceCalculator();
        Assert.Throws<ArgumentNullException>(() => calc.Compute("hello", null!));
    }

    // ── Saturating coverage (ADR-0017 follow-up 2026-06-10) ─────────────
    // Tool-trace citations are answer-level artifacts; one citation covers
    // up to ParagraphsPerExpectedCitation (4) paragraphs. These tests pin
    // the formula min(1, citations / ceil(paragraphs / 4)) against the two
    // live false refusals (ev-valuation-0001 c=0.17, ev-repair-0001 c=0.20).

    private static string Paragraphs(int count) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"Paragraph {i} with a factual claim."));

    [Fact]
    public void Compute_SixParagraphsOneCitation_CoverageIsHalf_AndPassesThreshold()
    {
        // The ev-valuation-0001 shape: well-grounded single-source answer
        // formatted as six paragraphs. Old formula: 1/6 = 0.17 → refusal.
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute(Paragraphs(6), [OpdbCitation]);

        Assert.Equal(0.5, signals.CitationCoverage, precision: 6);
        Assert.True(signals.Composite() >= 0.65);
    }

    [Fact]
    public void Compute_FourParagraphsOneCitation_CoverageIsFull()
    {
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute(Paragraphs(4), [OpdbCitation]);

        Assert.Equal(1.0, signals.CitationCoverage, precision: 6);
    }

    [Fact]
    public void Compute_FiveParagraphsOneCitation_CoverageIsHalf()
    {
        // ceil(5/4) = 2 expected citations; 1 present → 0.5.
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute(Paragraphs(5), [OpdbCitation]);

        Assert.Equal(0.5, signals.CitationCoverage, precision: 6);
    }

    [Fact]
    public void Compute_TwelveParagraphsOneCitation_JustPassesThreshold()
    {
        // The exact calibration boundary: ceil(12/4) = 3 expected,
        // 1/3 ≈ 0.333 → composite ≈ 0.657, a hair above 0.65. Pins the
        // ParagraphsPerExpectedCitation = 4 constant — a silent change to
        // 3 or 5 shifts this boundary and fails here.
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute(Paragraphs(12), [OpdbCitation]);

        Assert.Equal(1.0 / 3.0, signals.CitationCoverage, precision: 6);
        Assert.True(signals.Composite() >= 0.65);
    }

    [Fact]
    public void Compute_ThirteenParagraphsOneCitation_JustRefuses()
    {
        // First refusing length: ceil(13/4) = 4 expected, 1/4 = 0.25 →
        // composite ≈ 0.597 < 0.65.
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute(Paragraphs(13), [OpdbCitation]);

        Assert.Equal(0.25, signals.CitationCoverage, precision: 6);
        Assert.True(signals.Composite() < 0.65);
    }

    [Fact]
    public void Compute_SixteenParagraphsOneCitation_SprawlStillRefuses()
    {
        // The safety gradient must survive: a sprawling answer with one
        // token citation is exactly the thin-grounding case ADR-0017
        // guards against. ceil(16/4) = 4 → coverage 0.25 → composite
        // ∛(1.0 · 0.85 · 0.25) ≈ 0.60 < 0.65.
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute(Paragraphs(16), [OpdbCitation]);

        Assert.Equal(0.25, signals.CitationCoverage, precision: 6);
        Assert.True(signals.Composite() < 0.65);
    }

    [Fact]
    public void Compute_EightParagraphsTwoCitations_SaturatesAtFull()
    {
        var calc = new ConfidenceCalculator();
        var second = OpdbCitation with { MachineId = "GRBN-OTHER", SourceUrl = "https://opdb.org/machines/GRBN-OTHER" };
        var signals = calc.Compute(Paragraphs(8), [OpdbCitation, second]);

        Assert.Equal(1.0, signals.CitationCoverage, precision: 6);
    }

    [Fact]
    public void Compute_EmptyAnswerWithCitations_CoverageIsZero()
    {
        var calc = new ConfidenceCalculator();
        var signals = calc.Compute("   ", [OpdbCitation]);

        Assert.Equal(0.0, signals.CitationCoverage, precision: 6);
    }

    [Fact]
    public void Composite_GeometricMean_NearZeroSignalDrivesNearZero()
    {
        var signals = new ConfidenceSignals(
            RetrievalSimilarity: 1.0,
            ModelSelfReported: 1.0,
            CitationCoverage: 0.0); // floors to 0.05

        var composite = signals.Composite();
        // Floor of 0.05 means cube root of 0.05 ≈ 0.368
        Assert.True(composite < 0.40);
        Assert.True(composite > 0.30);
    }

    [Fact]
    public void Composite_AllOnes_ReturnsOne()
    {
        var signals = new ConfidenceSignals(1.0, 1.0, 1.0);
        Assert.Equal(1.0, signals.Composite(), precision: 6);
    }

    [Fact]
    public void Composite_AllAtFiftyPercent_ReturnsFiftyPercent()
    {
        var signals = new ConfidenceSignals(0.5, 0.5, 0.5);
        Assert.Equal(0.5, signals.Composite(), precision: 6);
    }

    [Fact]
    public void Composite_ClipsOutOfRangeInputs()
    {
        var negative = new ConfidenceSignals(-1.0, 0.5, 0.5).Composite();
        var aboveOne = new ConfidenceSignals(2.0, 0.5, 0.5).Composite();
        var nan = new ConfidenceSignals(double.NaN, 0.5, 0.5).Composite();

        // Negative + NaN clip to 0; aboveOne clips to 1.
        Assert.True(negative < 0.40); // dominated by 0.05 floor
        Assert.True(aboveOne > 0.5);
        Assert.True(nan < 0.40);
    }

    [Fact]
    public void CategorizeRefusal_NoCitationsAndLowRetrieval_ReturnsOutOfScope()
    {
        var calc = new ConfidenceCalculator();
        var signals = new ConfidenceSignals(
            RetrievalSimilarity: 0.5,
            ModelSelfReported: 0.85,
            CitationCoverage: 0.0);

        Assert.Equal(RefusalCategory.OutOfScope, calc.CategorizeRefusal(signals));
    }

    [Fact]
    public void CategorizeRefusal_LowModel_ReturnsLowModelConfidence()
    {
        var calc = new ConfidenceCalculator();
        var signals = new ConfidenceSignals(
            RetrievalSimilarity: 1.0,
            ModelSelfReported: 0.2,
            CitationCoverage: 0.7);

        Assert.Equal(RefusalCategory.LowModelConfidence, calc.CategorizeRefusal(signals));
    }

    [Fact]
    public void CategorizeRefusal_LowRetrievalDespiteCitation_ReturnsInsufficientGrounding()
    {
        var calc = new ConfidenceCalculator();
        var signals = new ConfidenceSignals(
            RetrievalSimilarity: 0.3,
            ModelSelfReported: 0.85,
            CitationCoverage: 0.6);

        Assert.Equal(RefusalCategory.InsufficientGrounding, calc.CategorizeRefusal(signals));
    }

    [Fact]
    public void CategorizeRefusal_NullSignals_Throws()
    {
        var calc = new ConfidenceCalculator();
        Assert.Throws<ArgumentNullException>(() => calc.CategorizeRefusal(null!));
    }
}
