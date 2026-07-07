using PinballWizard.Application.Ai.Retrieval;
using Xunit;

namespace PinballWizard.Application.Tests.Application.Ai;

public class RetrievalScoringTests
{
    [Fact]
    public void MaxRerankerScore_IsFour() =>
        Assert.Equal(4.0, RetrievalScoring.MaxRerankerScore);

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.12, 0.28)]   // the "28% match" card from the Cactus Canyon incident
    [InlineData(1.6, 0.40)]
    [InlineData(3.4, 0.85)]
    [InlineData(4.0, 1.0)]
    [InlineData(8.0, 1.0)]     // BM25 fallback above the ceiling clamps to 1.0
    [InlineData(-0.5, 0.0)]    // defensive: never negative
    public void NormalizeRerankerScore_MapsToClampedFraction(double raw, double expected) =>
        Assert.Equal(expected, RetrievalScoring.NormalizeRerankerScore(raw), precision: 6);
}
