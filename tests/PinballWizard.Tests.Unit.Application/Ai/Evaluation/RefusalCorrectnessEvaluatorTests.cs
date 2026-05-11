using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Tests.Unit.Application.Ai.Evaluation;

public sealed class RefusalCorrectnessEvaluatorTests
{
    private readonly RefusalCorrectnessEvaluator _evaluator = new();

    [Fact]
    public void Compute_BothRefused_Returns1()
    {
        // Out-of-scope question, agent correctly refused.
        Assert.Equal(1.0, _evaluator.Compute(predictedRefusal: true, acceptableRefusal: true));
    }

    [Fact]
    public void Compute_NeitherRefused_Returns1()
    {
        // Grounded question, agent correctly answered.
        Assert.Equal(1.0, _evaluator.Compute(predictedRefusal: false, acceptableRefusal: false));
    }

    [Fact]
    public void Compute_OverEagerAnswer_OnRefusableQuestion_Returns0()
    {
        // Out-of-scope question, agent fabricated an answer instead of
        // refusing — exactly the failure mode ADR-0017 guards against.
        Assert.Equal(0.0, _evaluator.Compute(predictedRefusal: false, acceptableRefusal: true));
    }

    [Fact]
    public void Compute_OverEagerRefusal_OnAnswerableQuestion_Returns0()
    {
        // Symmetric concern: the agent refused a question it should
        // have been able to answer — also a regression.
        Assert.Equal(0.0, _evaluator.Compute(predictedRefusal: true, acceptableRefusal: false));
    }
}
