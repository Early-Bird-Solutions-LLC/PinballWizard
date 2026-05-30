using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

// W4-2 evaluator. Mirrors the paragraph-fraction heuristic in
// `ConfidenceCalculator.ComputeCitationCoverage`. The two implementations
// must move together — a sibling-diff finding in the PR self-audit fires
// when one is edited without the other.
public sealed class CitationCoverageEvaluatorTests
{
    private static readonly string[] OneCitation = ["GRBN-MQR4P"];
    private static readonly string[] TwoCitations = ["GRBN-MQR4P", "G6E0E-MEooN"];
    private static readonly string[] FourCitations = ["A", "B", "C", "D"];
    private static readonly string[] Empty = [];

    private const string OneParagraph =
        "Foo Fighters is a Stern Pinball machine from 2023.";

    private const string TwoParagraphs =
        "Foo Fighters is a Stern Pinball machine from 2023.\n\n"
        + "Designed by George Gomez, the Pro edition's MSRP was $7,000.";

    private const string FourParagraphs =
        "Para 1.\n\nPara 2.\n\nPara 3.\n\nPara 4.";

    private readonly CitationCoverageEvaluator _evaluator = new();

    [Fact]
    public void Compute_OneParagraphOneCitation_ReturnsFullCoverage()
    {
        var score = _evaluator.Compute(OneParagraph, OneCitation);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_TwoParagraphsOneCitation_ReturnsHalfCoverage()
    {
        // 1 citation / 2 paragraphs = 0.5
        var score = _evaluator.Compute(TwoParagraphs, OneCitation);

        Assert.Equal(0.5, score);
    }

    [Fact]
    public void Compute_TwoParagraphsTwoCitations_ReturnsFullCoverage()
    {
        var score = _evaluator.Compute(TwoParagraphs, TwoCitations);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_FourParagraphsOneCitation_ReturnsQuarterCoverage()
    {
        var score = _evaluator.Compute(FourParagraphs, OneCitation);

        Assert.Equal(0.25, score);
    }

    [Fact]
    public void Compute_OneParagraphFourCitations_ClampedToOne()
    {
        // Multiple citations in a single paragraph don't compound past
        // 1.0 — the metric is "did at least one citation back the
        // paragraph?" The clamp prevents over-citing from gaming the
        // signal.
        var score = _evaluator.Compute(OneParagraph, FourCitations);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_NoCitations_ReturnsZero()
    {
        // Honest score for an uncited answer. The user-visible refusal
        // posture is governed by ADR-0023 at the AiRouter layer; at the
        // eval layer, an un-refused uncited answer scores 0.
        var score = _evaluator.Compute(OneParagraph, Empty);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_EmptyAnswerWithCitations_ReturnsZero()
    {
        var score = _evaluator.Compute(string.Empty, OneCitation);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_WhitespaceAnswerWithCitations_ReturnsZero()
    {
        var score = _evaluator.Compute("   \n\n   ", OneCitation);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_BothEmpty_ReturnsZero()
    {
        var score = _evaluator.Compute(string.Empty, Empty);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_CrLfDoubleNewline_TreatedAsParagraphSeparator()
    {
        // Cross-platform paragraph boundary: \r\n\r\n splits the same as
        // \n\n. Pinned to the same Split tokens the
        // ConfidenceCalculator heuristic uses.
        var crlfTwoParagraphs = "Para 1.\r\n\r\nPara 2.";
        var score = _evaluator.Compute(crlfTwoParagraphs, OneCitation);

        Assert.Equal(0.5, score);
    }

    [Fact]
    public void Compute_NullPredicted_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute("answer", null!));
    }

    [Fact]
    public void Compute_NullAnswerText_TreatedAsEmpty()
    {
        // Defensive — null answer is the WizardAnswer.Text == null path
        // (shouldn't happen but the harness defaults to empty string
        // there). Honor the same "no signal → 0.0" posture as
        // string.Empty rather than throwing.
        var score = _evaluator.Compute(null!, OneCitation);

        Assert.Equal(0.0, score);
    }
}
