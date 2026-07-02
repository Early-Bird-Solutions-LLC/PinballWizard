using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation.Findability;

public sealed class MrrEvaluatorTests
{
    private readonly MrrEvaluator _evaluator = new();

    // ── Core reciprocal-rank math ─────────────────────────────────────────

    [Fact]
    public void ComputeReciprocalRank_FirstHitAtRank1_Returns1()
    {
        // A is the first candidate → RR = 1/1 = 1.0
        var rr = _evaluator.ComputeReciprocalRank(["A", "B", "C"], ["A"]);

        Assert.Equal(1.0, rr);
    }

    [Fact]
    public void ComputeReciprocalRank_FirstHitAtRank2_ReturnsHalf()
    {
        // A is at position 2 → RR = 1/2 = 0.5
        var rr = _evaluator.ComputeReciprocalRank(["B", "A", "C"], ["A"]);

        Assert.Equal(0.5, rr);
    }

    [Fact]
    public void ComputeReciprocalRank_FirstHitAtRank3_ReturnsOneThird()
    {
        // A is at position 3 → RR = 1/3
        var rr = _evaluator.ComputeReciprocalRank(["C", "B", "A"], ["A"]);

        Assert.Equal(1.0 / 3.0, rr, precision: 10);
    }

    [Fact]
    public void ComputeReciprocalRank_NoHit_Returns0()
    {
        // Expected machine not retrieved at all
        var rr = _evaluator.ComputeReciprocalRank(["B", "C", "D"], ["A"]);

        Assert.Equal(0.0, rr);
    }

    // ── Multiple expected IDs ─────────────────────────────────────────────

    [Fact]
    public void ComputeReciprocalRank_MultipleExpected_EarliestHitDeterminesRank()
    {
        // A is at rank 3, B is at rank 2 → first hit encountered is B at rank 2
        var rr = _evaluator.ComputeReciprocalRank(["C", "B", "A"], ["A", "B"]);

        Assert.Equal(0.5, rr);
    }

    [Fact]
    public void ComputeReciprocalRank_MultipleExpected_FirstRanked_Returns1()
    {
        // Both expected are in top-2; first hit A is at rank 1
        var rr = _evaluator.ComputeReciprocalRank(["A", "B", "C"], ["A", "B"]);

        Assert.Equal(1.0, rr);
    }

    // ── Edge cases ────────────────────────────────────────────────────────

    [Fact]
    public void ComputeReciprocalRank_EmptyExpected_Returns1()
    {
        // Undefined metric: nothing to find → 1.0 by convention
        var rr = _evaluator.ComputeReciprocalRank(["A", "B"], []);

        Assert.Equal(1.0, rr);
    }

    [Fact]
    public void ComputeReciprocalRank_EmptyCandidates_NonEmptyExpected_Returns0()
    {
        var rr = _evaluator.ComputeReciprocalRank([], ["A"]);

        Assert.Equal(0.0, rr);
    }

    [Fact]
    public void ComputeReciprocalRank_CaseInsensitiveMatch()
    {
        var rr = _evaluator.ComputeReciprocalRank(["fake-aaaa1"], ["FAKE-AAAA1"]);

        Assert.Equal(1.0, rr);
    }

    // ── Guard clauses ─────────────────────────────────────────────────────

    [Fact]
    public void ComputeReciprocalRank_NullCandidates_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.ComputeReciprocalRank(null!, ["A"]));
    }

    [Fact]
    public void ComputeReciprocalRank_NullExpected_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.ComputeReciprocalRank(["A"], null!));
    }
}
