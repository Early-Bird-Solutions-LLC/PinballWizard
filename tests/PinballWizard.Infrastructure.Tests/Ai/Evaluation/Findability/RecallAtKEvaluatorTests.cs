using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation.Findability;

public sealed class RecallAtKEvaluatorTests
{
    private readonly RecallAtKEvaluator _evaluator = new();

    // ── Recall@1 ─────────────────────────────────────────────────────────

    [Fact]
    public void RecallAt1_CorrectItemFirst_Returns1()
    {
        // A is at rank 1 → fully recalled in top-1
        var score = _evaluator.Compute(["A", "B", "C"], ["A"], k: 1);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void RecallAt1_CorrectItemSecond_Returns0()
    {
        // A is at rank 2 — outside top-1 window
        var score = _evaluator.Compute(["B", "A", "C"], ["A"], k: 1);

        Assert.Equal(0.0, score);
    }

    // ── Recall@k (full depth) ────────────────────────────────────────────

    [Fact]
    public void RecallAtK_AllExpectedInTopK_Returns1()
    {
        // A and B are both in top-2; 2/2 = 1.0
        var score = _evaluator.Compute(["A", "B", "C"], ["A", "B"], k: 2);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void RecallAtK_HalfExpectedInTopK_Returns0Point5()
    {
        // A is in top-2 but B is not; 1/2 = 0.5
        var score = _evaluator.Compute(["A", "C", "B"], ["A", "B"], k: 2);

        Assert.Equal(0.5, score);
    }

    [Fact]
    public void RecallAtK_NoneExpectedInTopK_Returns0()
    {
        // Neither A nor B appears in top-3 results
        var score = _evaluator.Compute(["C", "D", "E"], ["A", "B"], k: 3);

        Assert.Equal(0.0, score);
    }

    // ── Edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void RecallAtK_EmptyExpected_Returns1()
    {
        // Undefined metric: nothing expected → 1.0 by convention
        var score = _evaluator.Compute(["A", "B"], [], k: 2);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void RecallAtK_EmptyCandidates_NonEmptyExpected_Returns0()
    {
        // No candidates returned; nothing can be recalled
        var score = _evaluator.Compute([], ["A"], k: 5);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void RecallAtK_KExceedsCandidateCount_EvaluatesAllCandidates()
    {
        // Only 2 candidates, k=10; A is present → full recall
        var score = _evaluator.Compute(["A", "B"], ["A"], k: 10);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void RecallAtK_CaseInsensitiveMatch()
    {
        var score = _evaluator.Compute(["fake-aaaa1", "X"], ["FAKE-AAAA1"], k: 1);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void RecallAtK_DuplicateExpectedIds_CountedOnce()
    {
        // "A" appears twice in expected → set de-duplication; 1/1 = 1.0, not 1/2
        var score = _evaluator.Compute(["A", "B"], ["A", "A"], k: 1);

        Assert.Equal(1.0, score);
    }

    // ── Guard clauses ────────────────────────────────────────────────────

    [Fact]
    public void Compute_NullCandidates_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(null!, ["A"], k: 1));
    }

    [Fact]
    public void Compute_NullExpected_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(["A"], null!, k: 1));
    }

    [Fact]
    public void Compute_KZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _evaluator.Compute(["A"], ["A"], k: 0));
    }

    [Fact]
    public void Compute_KNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _evaluator.Compute(["A"], ["A"], k: -1));
    }

    [Fact]
    public void Compute_DuplicateCandidate_DoesNotExceedOne()
    {
        // A misbehaving lookup repeats the one relevant id. Each expected id
        // counts at most once — recall stays 1.0, never 2.0.
        var recall = _evaluator.Compute(["A", "A"], ["A"], k: 2);
        Assert.Equal(1.0, recall);
    }

    [Fact]
    public void Compute_DuplicateCandidate_WithMultipleExpected_CountsEachOnce()
    {
        // ["A","A","B"] against expected {A,B}: the duplicate A does not inflate;
        // both distinct expected ids are found → 1.0.
        var recall = _evaluator.Compute(["A", "A", "B"], ["A", "B"], k: 3);
        Assert.Equal(1.0, recall);
    }
}
