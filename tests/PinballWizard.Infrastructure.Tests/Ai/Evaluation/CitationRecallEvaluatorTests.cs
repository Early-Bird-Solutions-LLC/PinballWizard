using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

public sealed class CitationRecallEvaluatorTests
{
    private static readonly string[] OneCorrect = ["GRBN-MQR4P"];
    private static readonly string[] OneIncorrect = ["WRONG-XXXX"];
    private static readonly string[] TwoCorrect = ["GRBN-MQR4P", "G6E0E-MEooN"];
    private static readonly string[] OneCorrectOneExtra = ["GRBN-MQR4P", "EXTRA-ZZZZ"];
    private static readonly string[] OneCorrectOneMissing = ["GRBN-MQR4P", "MISSING-YYYY"];
    private static readonly string[] CorrectLowercase = ["grbn-mqr4p"];
    private static readonly string[] CorrectPlusBonus = ["GRBN-MQR4P", "EXTRA-ZZZZ", "BONUS-AAAA"];
    private static readonly string[] Empty = [];

    private readonly CitationRecallEvaluator _evaluator = new();

    [Fact]
    public void Compute_FullOverlap_Returns1()
    {
        var score = _evaluator.Compute(TwoCorrect, TwoCorrect);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_PartialOverlap_ReturnsHitsOverExpectedCount()
    {
        // 1 of 2 expected found in predicted → recall = 0.5
        var score = _evaluator.Compute(OneCorrectOneExtra, OneCorrectOneMissing);

        Assert.Equal(0.5, score);
    }

    [Fact]
    public void Compute_NoOverlap_Returns0()
    {
        var score = _evaluator.Compute(OneIncorrect, OneCorrect);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_EmptyPredicted_NonEmptyExpected_Returns0()
    {
        // Nothing recalled — recall = 0.
        var score = _evaluator.Compute(Empty, OneCorrect);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_EmptyExpected_Returns1()
    {
        // No expected citations — recall is undefined; the evaluator
        // returns 1.0 (the refusal-honored case). Hallucination
        // against an out-of-scope question is penalized by precision.
        var score = _evaluator.Compute(OneCorrect, Empty);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_BothEmpty_Returns1()
    {
        var score = _evaluator.Compute(Empty, Empty);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_CaseInsensitive_FullMatch()
    {
        var score = _evaluator.Compute(CorrectLowercase, OneCorrect);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_PredictedSupersetOfExpected_ReturnsFullRecall()
    {
        // Predicted has extras; recall ignores those — only counts
        // whether each expected was hit.
        var score = _evaluator.Compute(CorrectPlusBonus, OneCorrect);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_NullPredicted_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(null!, OneCorrect));
    }

    [Fact]
    public void Compute_NullExpected_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(OneCorrect, null!));
    }

    // ── Any-of acceptable-citation-sets (AB#259 edition-aware) ──────────

    private static readonly string[] ProBase = ["GweeP-MW95j"];
    private static readonly string[] PremLeBase = ["GweeP-Ml9pZ"];
    private static readonly string[] BothBases = ["GweeP-MW95j", "GweeP-Ml9pZ"];

    [Fact]
    public void ComputeAnyOf_PredictionMatchesOneAcceptableSet_FullRecall()
    {
        // R1: either base alone. Predicting Pro recalls the [Pro] set fully.
        IReadOnlyList<IReadOnlyList<string>> acceptable = [ProBase, PremLeBase];

        var score = _evaluator.ComputeAnyOf(ProBase, acceptable);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void ComputeAnyOf_PicksBestScoringAcceptableSet()
    {
        // Predicting Prem/LE recalls the [Prem/LE] set fully; best-of
        // beats the 0.0 it scores against [Pro].
        IReadOnlyList<IReadOnlyList<string>> acceptable = [ProBase, PremLeBase];

        var score = _evaluator.ComputeAnyOf(PremLeBase, acceptable);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void ComputeAnyOf_SubsetSet_PartialRecallWhenOneOfTwoCited()
    {
        // edition-subset row needs BOTH bases. Citing only one → recall 0.5
        // against that single acceptable set.
        IReadOnlyList<IReadOnlyList<string>> acceptable = [BothBases];

        var score = _evaluator.ComputeAnyOf(ProBase, acceptable);

        Assert.Equal(0.5, score);
    }

    [Fact]
    public void ComputeAnyOf_EmptyAcceptableList_TreatedAsNoExpected_Returns1()
    {
        // No acceptable sets → recall undefined → 1.0 (refusal-honored),
        // matching the single-set Compute contract for empty expected.
        IReadOnlyList<IReadOnlyList<string>> none = [];

        Assert.Equal(1.0, _evaluator.ComputeAnyOf(Empty, none));
        Assert.Equal(1.0, _evaluator.ComputeAnyOf(OneCorrect, none));
    }

    [Fact]
    public void ComputeAnyOf_NullAcceptableList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.ComputeAnyOf(OneCorrect, null!));
    }
}
