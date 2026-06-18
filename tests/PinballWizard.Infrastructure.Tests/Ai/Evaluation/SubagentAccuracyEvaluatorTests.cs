using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

public sealed class SubagentAccuracyEvaluatorTests
{
    private readonly SubagentAccuracyEvaluator _evaluator = new();

    [Theory]
    [InlineData("Rules", "Rules")]
    [InlineData("Valuation", "Valuation")]
    [InlineData("Repair", "Repair")]
    [InlineData("Wizard", "Wizard")]
    public void Compute_ExactMatch_Returns1(string predicted, string expected)
    {
        Assert.Equal(1.0, _evaluator.Compute(predicted, expected));
    }

    [Theory]
    [InlineData("Rules", "Valuation")]
    [InlineData("Repair", "Rules")]
    [InlineData("Wizard", "Repair")]
    public void Compute_Mismatch_Returns0(string predicted, string expected)
    {
        Assert.Equal(0.0, _evaluator.Compute(predicted, expected));
    }

    [Theory]
    [InlineData("rules", "Rules")]
    [InlineData("RULES", "Rules")]
    [InlineData("Rules", "rUlEs")]
    public void Compute_CaseInsensitive_TreatedAsMatch(string predicted, string expected)
    {
        Assert.Equal(1.0, _evaluator.Compute(predicted, expected));
    }

    [Theory]
    [InlineData("  Rules  ", "Rules")]
    [InlineData("Rules", "  Rules  ")]
    public void Compute_LeadingTrailingWhitespace_Trimmed(string predicted, string expected)
    {
        Assert.Equal(1.0, _evaluator.Compute(predicted, expected));
    }

    [Fact]
    public void Compute_BothEmpty_Returns1()
    {
        Assert.Equal(1.0, _evaluator.Compute(string.Empty, string.Empty));
    }

    [Fact]
    public void Compute_NullPredicted_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _evaluator.Compute(null!, "Rules"));
    }

    [Fact]
    public void Compute_NullExpected_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _evaluator.Compute("Rules", null!));
    }

    // ── acceptable_sub_agents extension (AB#259) ───────────────────────

    // (a) Wizard answered directly (getMachineByTitle path) on a question
    // annotated with acceptable_sub_agents=["Wizard"] — must score correct.
    // This is the canonical fix for the measurement artifact: theme/edition/
    // MSRP-from-OPDB questions where direct Wizard answers were being counted
    // as routing failures.
    [Fact]
    public void Compute_WithAcceptable_PredictedInAcceptableList_Returns1()
    {
        // expected = Valuation, but Wizard is in the acceptable list
        // because the answer comes from OPDB machine data directly.
        var result = _evaluator.Compute("Wizard", "Valuation", ["Wizard"]);
        Assert.Equal(1.0, result);
    }

    // (b) Predicted=Wizard, no acceptable_sub_agents annotation — must score
    // incorrect (default exact-match behavior unchanged).
    [Fact]
    public void Compute_WithNoAcceptableField_WizardPredictedRulesExpected_Returns0()
    {
        // The ground-truth row has no acceptable_sub_agents (null) — exact
        // match only. Wizard answering a corpus-retrieval question is a miss.
        var result = _evaluator.Compute("Wizard", "Rules", null);
        Assert.Equal(0.0, result);
    }

    // (c) Regression: predicted=Rules, expected=Rules, no acceptable list —
    // exact-match still scores 1.0.
    [Fact]
    public void Compute_WithAcceptableList_ExactMatchOnExpected_Returns1()
    {
        var result = _evaluator.Compute("Rules", "Rules", ["Wizard"]);
        Assert.Equal(1.0, result);
    }

    // (d) Empty acceptable list behaves identically to null (degrades to
    // exact-match path) — no regressions from zero-length annotation.
    [Fact]
    public void Compute_WithEmptyAcceptableList_DegradesToExactMatch()
    {
        Assert.Equal(0.0, _evaluator.Compute("Wizard", "Rules", []));
        Assert.Equal(1.0, _evaluator.Compute("Rules", "Rules", []));
    }

    // Acceptable-list matching is also case-insensitive and trims whitespace.
    [Fact]
    public void Compute_WithAcceptable_CaseInsensitiveAndTrimmed()
    {
        Assert.Equal(1.0, _evaluator.Compute("wizard", "Valuation", ["Wizard"]));
        Assert.Equal(1.0, _evaluator.Compute("  Wizard  ", "Valuation", ["wizard"]));
    }
}
