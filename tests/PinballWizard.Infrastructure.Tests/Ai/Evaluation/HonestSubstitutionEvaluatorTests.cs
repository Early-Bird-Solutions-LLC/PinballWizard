using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

// R3 evaluator (AB#259): the user named an edition we have no data for.
// A passing answer DISCLOSES the named edition is absent AND cites a
// substitute. Silent substitution and blanket refusal both FAIL.
public sealed class HonestSubstitutionEvaluatorTests
{
    private static readonly string[] Substitute = ["GweeP-MW95j"];
    private static readonly string[] Empty = [];

    private const string DisclosedAndCited =
        "I don't have LE-specific details for that, but here's what the Pro manual says " +
        "(cited: Godzilla Pro Manual): the flipper coil is a 23-800.";

    private const string SilentSubstitution =
        "The flipper coil is a 23-800 (cited: Godzilla Pro Manual).";

    private const string DisclosedButUncited =
        "I don't have LE-specific details for that machine.";

    private readonly HonestSubstitutionEvaluator _evaluator = new();

    [Fact]
    public void Compute_DisclosesAbsenceAndCitesSubstitute_Returns1()
    {
        var score = _evaluator.Compute(DisclosedAndCited, Substitute, "LE");

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_SilentSubstitution_NoDisclosure_Returns0()
    {
        // Cited a substitute but never disclosed the LE gap → FAIL.
        var score = _evaluator.Compute(SilentSubstitution, Substitute, "LE");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_DisclosedButNoCitation_Returns0()
    {
        // Disclosed the gap but cited nothing → blanket refusal → FAIL.
        var score = _evaluator.Compute(DisclosedButUncited, Empty, "LE");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_DisclosurePhrase_IsntAvailable_Accepted()
    {
        const string answer =
            "LE data isn't available for this title, but the Pro manual covers it (cited: Godzilla Pro Manual).";

        var score = _evaluator.Compute(answer, Substitute, "LE");

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_DisclosureIsCaseInsensitive()
    {
        const string answer =
            "I DON'T HAVE LE details, but here's the Pro manual (cited: Godzilla Pro Manual).";

        var score = _evaluator.Compute(answer, Substitute, "LE");

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_NullAnswer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(null!, Substitute, "LE"));
    }

    [Fact]
    public void Compute_NullPredicted_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Compute(DisclosedAndCited, null!, "LE"));
    }

    [Fact]
    public void Compute_NullOrEmptyNamedEdition_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _evaluator.Compute(DisclosedAndCited, Substitute, ""));
    }

    // ── Word-boundary regression (AB#259 code-review) ───────────────────
    // "LE" must NOT match as a substring of "avaiLAble"/"available". An
    // answer that discloses + cites but never NAMES the LE edition must
    // score 0.0, even though "not available" both is a disclosure phrase
    // AND contains the substring "le".

    [Fact]
    public void Compute_DisclosurePhraseContainsLeSubstring_ButEditionNotNamed_Returns0()
    {
        const string answer =
            "That data is not available, but here's the Pro manual (cited: Godzilla Pro Manual).";

        var score = _evaluator.Compute(answer, Substitute, "LE");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Compute_LeAsWholeWord_IsNamed_Returns1()
    {
        const string answer =
            "I don't have LE details for that, but the Pro manual covers it (cited: Godzilla Pro Manual).";

        var score = _evaluator.Compute(answer, Substitute, "LE");

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Compute_LeInQualifiedPhrase_IsNamed_Returns1()
    {
        // Hyphenated qualifier "LE-specific" must still count as naming LE
        // (the hyphen is a word boundary). Disclosure carried by "isn't
        // available".
        const string answer =
            "LE-specific data isn't available, but the Pro manual covers it (cited: Godzilla Pro Manual).";

        var score = _evaluator.Compute(answer, Substitute, "LE");

        Assert.Equal(1.0, score);
    }
}
