using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Confidence;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

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
