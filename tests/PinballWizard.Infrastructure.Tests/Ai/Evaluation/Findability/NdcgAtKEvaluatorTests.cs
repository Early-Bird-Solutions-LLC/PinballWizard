using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation.Findability;

// All expected values are derived from the standard IR NDCG formula:
//   DCG@k  = Σ_{i=1}^{k} (2^rel_i − 1) / log₂(i+1)
//   IDCG@k = DCG@k of the ideal ordering
//   NDCG@k = DCG@k / IDCG@k
//
// log₂(2) = 1.0, log₂(3) = 1.5849625007211563, log₂(4) = 2.0
public sealed class NdcgAtKEvaluatorTests
{
    private readonly NdcgAtKEvaluator _evaluator = new();

    // ── Binary overload ───────────────────────────────────────────────────

    [Fact]
    public void Ndcg_Binary_PerfectRanking_ReturnsOne()
    {
        // A is expected (grade 1), ranked 1st → DCG = IDCG → NDCG = 1.0
        var score = _evaluator.Compute(["A", "B", "C"], ["A"], k: 3);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Ndcg_Binary_RelevantItemAtPosition2_MatchesFormula()
    {
        // A is expected, ranked 2nd (B is irrelevant at rank 1)
        // DCG@3  = 0/log₂(2) + (2^1−1)/log₂(3) + 0 = 1/log₂(3)
        // IDCG@3 = (2^1−1)/log₂(2) = 1.0
        // NDCG@3 = 1/log₂(3)
        var score = _evaluator.Compute(["B", "A", "C"], ["A"], k: 3);

        var expected = 1.0 / Math.Log2(3);
        Assert.Equal(expected, score, precision: 10);
    }

    [Fact]
    public void Ndcg_Binary_RelevantItemAtPosition3_MatchesFormula()
    {
        // A is expected, ranked 3rd
        // DCG@3  = (2^1−1)/log₂(4) = 1/2 = 0.5
        // IDCG@3 = (2^1−1)/log₂(2) = 1.0
        // NDCG@3 = 0.5
        var score = _evaluator.Compute(["B", "C", "A"], ["A"], k: 3);

        Assert.Equal(0.5, score, precision: 10);
    }

    [Fact]
    public void Ndcg_Binary_NoRelevantItemInTopK_Returns0()
    {
        // A is expected but not in the candidate list
        var score = _evaluator.Compute(["B", "C", "D"], ["A"], k: 3);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Ndcg_Binary_TwoExpectedBothPerfect_ReturnsOne()
    {
        // A and B are both expected; ranked [A, B, C] → perfect ordering
        // DCG@2  = 1/log₂(2) + 1/log₂(3)
        // IDCG@2 = 1/log₂(2) + 1/log₂(3)  (same — already ideal)
        // NDCG@2 = 1.0
        var score = _evaluator.Compute(["A", "B", "C"], ["A", "B"], k: 2);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Ndcg_Binary_TwoExpectedReversed_MatchesFormula()
    {
        // A and B expected; ranked [B, A] → swapped from ideal
        // DCG@2  = 1/log₂(2) + 1/log₂(3) = 1 + 1/log₂(3)  (same either way: both grade-1)
        // IDCG@2 = same
        // NDCG@2 = 1.0  (binary-grade items are order-invariant when both retrieved)
        var score = _evaluator.Compute(["B", "A", "C"], ["A", "B"], k: 2);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Ndcg_Binary_EmptyExpected_Returns1()
    {
        // No relevant items: IDCG = 0 → undefined metric → 1.0 by convention
        var score = _evaluator.Compute(["A", "B"], (IReadOnlyCollection<string>)[], k: 2);

        Assert.Equal(1.0, score);
    }

    // ── Graded overload ───────────────────────────────────────────────────

    [Fact]
    public void Ndcg_Graded_PerfectRanking_ReturnsOne()
    {
        // A=grade 3, B=grade 1; ranked [A, B] → optimal order
        // DCG@2  = (2^3−1)/log₂(2) + (2^1−1)/log₂(3) = 7 + 1/log₂(3)
        // IDCG@2 = same (A before B is the ideal)
        // NDCG@2 = 1.0
        var grades = new Dictionary<string, int> { ["A"] = 3, ["B"] = 1 };

        var score = _evaluator.Compute(["A", "B"], grades, k: 2);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Ndcg_Graded_ImperfectRanking_MatchesFormula()
    {
        // A=grade 3, B=grade 1; ranked [B, A] → sub-optimal (high-grade item buried)
        // DCG@2  = (2^1−1)/log₂(2) + (2^3−1)/log₂(3) = 1 + 7/log₂(3)
        // IDCG@2 = (2^3−1)/log₂(2) + (2^1−1)/log₂(3) = 7 + 1/log₂(3)
        // NDCG@2 = (1 + 7/log₂(3)) / (7 + 1/log₂(3))
        var grades = new Dictionary<string, int> { ["A"] = 3, ["B"] = 1 };

        var score = _evaluator.Compute(["B", "A"], grades, k: 2);

        var dcg = 1.0 + 7.0 / Math.Log2(3);
        var idcg = 7.0 + 1.0 / Math.Log2(3);
        var expected = dcg / idcg;
        Assert.Equal(expected, score, precision: 10);
    }

    [Fact]
    public void Ndcg_Graded_OnlyHighGradeRetrieved_MatchesFormula()
    {
        // A=grade 3 in the candidate list; B=grade 2 is NOT retrieved
        // k=2, candidates = [A, C(irrelevant)]
        // DCG@2  = (2^3−1)/log₂(2) + 0 = 7
        // IDCG@2 = (2^3−1)/log₂(2) + (2^2−1)/log₂(3) = 7 + 3/log₂(3)
        // NDCG@2 = 7 / (7 + 3/log₂(3))
        var grades = new Dictionary<string, int> { ["A"] = 3, ["B"] = 2 };

        var score = _evaluator.Compute(["A", "C"], grades, k: 2);

        var idcg = 7.0 + 3.0 / Math.Log2(3);
        var expected = 7.0 / idcg;
        Assert.Equal(expected, score, precision: 10);
    }

    [Fact]
    public void Ndcg_Graded_EmptyGrades_Returns1()
    {
        // IDCG = 0 (no relevant items) → undefined metric → 1.0
        var grades = new Dictionary<string, int>();

        var score = _evaluator.Compute(["A", "B"], grades, k: 3);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Ndcg_Graded_CaseInsensitiveIdMatching()
    {
        // Grade is keyed with uppercase; candidate list uses lowercase
        var grades = new Dictionary<string, int> { ["FAKE-AAAA1"] = 3 };

        var score = _evaluator.Compute(["fake-aaaa1", "X"], grades, k: 2);

        // A=grade 3 at position 1 → DCG = IDCG → NDCG = 1.0
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Ndcg_Graded_AllZeroGrades_Returns1()
    {
        // All grades 0 → IDCG = 0 → undefined → 1.0
        var grades = new Dictionary<string, int> { ["A"] = 0 };

        var score = _evaluator.Compute(["A", "B"], grades, k: 2);

        Assert.Equal(1.0, score);
    }

    // ── Guard clauses ─────────────────────────────────────────────────────

    [Fact]
    public void Compute_Graded_NullCandidates_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(null!, new Dictionary<string, int> { ["A"] = 1 }, k: 1));
    }

    [Fact]
    public void Compute_Graded_NullGrades_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(["A"], (IReadOnlyDictionary<string, int>)null!, k: 1));
    }

    [Fact]
    public void Compute_Graded_KZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _evaluator.Compute(["A"], new Dictionary<string, int> { ["A"] = 1 }, k: 0));
    }

    [Fact]
    public void Compute_Binary_NullCandidates_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(null!, ["A"], k: 1));
    }

    [Fact]
    public void Compute_Binary_NullExpected_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(["A"], (IReadOnlyCollection<string>)null!, k: 1));
    }
}
