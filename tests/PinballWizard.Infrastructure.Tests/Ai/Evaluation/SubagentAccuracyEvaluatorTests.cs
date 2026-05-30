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
}
