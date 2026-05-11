using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Tests.Unit.Application.Ai.Evaluation;

public sealed class CitationPrecisionEvaluatorTests
{
    private static readonly string[] OneCorrect = ["GRBN-MQR4P"];
    private static readonly string[] OneIncorrect = ["WRONG-XXXX"];
    private static readonly string[] TwoCorrect = ["GRBN-MQR4P", "G6E0E-MEooN"];
    private static readonly string[] OneCorrectOneWrong = ["GRBN-MQR4P", "WRONG-XXXX"];
    private static readonly string[] OneCorrectOneOther = ["GRBN-MQR4P", "OTHER-YYYY"];
    private static readonly string[] CorrectLowercase = ["grbn-mqr4p"];
    private static readonly string[] DuplicatedCorrect = ["GRBN-MQR4P", "GRBN-MQR4P"];
    private static readonly string[] Empty = [];

    private readonly CitationPrecisionEvaluator _evaluator = new();

    [Fact]
    public void Compute_FullOverlap_Returns1()
    {
        var score = _evaluator.Compute(TwoCorrect, TwoCorrect);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_PartialOverlap_ReturnsHitsOverPredictedCount()
    {
        // 1 of 2 predicted is correct → precision = 0.5
        var score = _evaluator.Compute(OneCorrectOneWrong, OneCorrectOneOther);

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
        var score = _evaluator.Compute(Empty, OneCorrect);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_EmptyExpected_NonEmptyPredicted_Returns0()
    {
        // Hallucinated citation against an out-of-scope question — precision = 0.
        var score = _evaluator.Compute(OneCorrect, Empty);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_BothEmpty_Returns1()
    {
        // Refusal honored — no citation expected, no citation predicted.
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
    public void Compute_DuplicatesInPredicted_DeduplicatedBeforeScoring()
    {
        // Two duplicates of the same correct id should not double-count
        // — set semantics: precision = 1/1 = 1.0, not 1/2.
        var score = _evaluator.Compute(DuplicatedCorrect, OneCorrect);

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
}
