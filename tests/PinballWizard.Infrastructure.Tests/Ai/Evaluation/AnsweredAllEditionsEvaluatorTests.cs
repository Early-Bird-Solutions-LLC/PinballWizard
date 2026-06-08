using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

// R2 evaluator (AB#259): the answer must address every required edition
// in ONE response, attributed per edition, with a citation per edition.
public sealed class AnsweredAllEditionsEvaluatorTests
{
    private static readonly string[] BothBases = ["GweeP-MW95j", "GweeP-Ml9pZ"];
    private static readonly string[] ProOnly = ["GweeP-MW95j"];
    private static readonly string[] RequiredEditions = ["Pro", "Premium/LE"];

    private const string TwoEditionAnswer =
        "For the Pro edition, multiball starts after three locks (cited: Godzilla Pro Manual). " +
        "For the Premium/LE edition, it starts after the magnet grab (cited: Godzilla Premium/LE Manual).";

    private const string OneEditionAnswer =
        "Multiball starts after three locks (cited: Godzilla Pro Manual).";

    private readonly AnsweredAllEditionsEvaluator _evaluator = new();

    [Fact]
    public void Compute_AllEditionsAttributed_AndCitedPerEdition_Returns1()
    {
        var score = _evaluator.Compute(TwoEditionAnswer, BothBases, RequiredEditions);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_MissingOneEditionLabel_Returns0()
    {
        // Premium/LE never named in the text → fails attribution.
        var score = _evaluator.Compute(OneEditionAnswer, BothBases, RequiredEditions);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_AttributesBothButCitesOnlyOneBase_Returns0()
    {
        // Both labels present in text but only one citation → cannot have
        // a source per edition.
        var score = _evaluator.Compute(TwoEditionAnswer, ProOnly, RequiredEditions);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_EditionLabelMatchIsCaseInsensitive()
    {
        const string lowered =
            "for the pro edition, X (cited: A). for the premium/le edition, Y (cited: B).";

        var score = _evaluator.Compute(lowered, BothBases, RequiredEditions);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_SlashSeparatedEditionLabel_MatchesEitherSubToken()
    {
        // "Premium/LE" should match an answer that says only "Premium" or
        // only "LE" — the slash denotes alternative spellings of one base.
        const string premiumOnlyLabel =
            "For the Pro edition, X (cited: A). For the Premium edition, Y (cited: B).";

        var score = _evaluator.Compute(premiumOnlyLabel, BothBases, RequiredEditions);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_NoRequiredEditions_Returns0()
    {
        // Misconfigured row: answered_all_editions with no required list.
        var score = _evaluator.Compute(TwoEditionAnswer, BothBases, []);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_NullAnswer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(null!, BothBases, RequiredEditions));
    }

    [Fact]
    public void Compute_NullPredicted_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(TwoEditionAnswer, null!, RequiredEditions));
    }

    [Fact]
    public void Compute_NullRequiredEditions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(TwoEditionAnswer, BothBases, null!));
    }

    // ── Word-boundary regression (AB#259 code-review) ───────────────────
    // "Pro" must NOT match as a substring of "appropriate"/"process". An
    // answer that uses such words but never NAMES the Pro edition must
    // score 0.0.

    [Fact]
    public void Compute_ProSubstringInOtherWords_ButEditionNotNamed_Returns0()
    {
        // "appropriate" and "process" both contain "pro"; "Premium/LE" is
        // named, but "Pro" is not → must fail attribution.
        const string answer =
            "Follow the appropriate process here (cited: A). " +
            "For the Premium/LE edition, do Y (cited: B).";

        var score = _evaluator.Compute(answer, BothBases, RequiredEditions);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_ProAsWholeWord_IsNamed_Returns1()
    {
        // Both editions named as whole words → pass.
        const string answer =
            "For the Pro edition, do X (cited: A). For the Premium/LE edition, do Y (cited: B).";

        var score = _evaluator.Compute(answer, BothBases, RequiredEditions);

        Assert.Equal(1.0, score);
    }
}
