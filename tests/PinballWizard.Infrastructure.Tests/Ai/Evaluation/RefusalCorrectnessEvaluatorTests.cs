using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

// Three-state semantics per the AB#259 metric-hygiene fix (2026-06-10):
// refusal_required rows must refuse, acceptable_refusal-only rows accept
// either behavior (null — no signal), all other rows must answer.
public sealed class RefusalCorrectnessEvaluatorTests
{
    private readonly RefusalCorrectnessEvaluator _evaluator = new();

    [Fact]
    public void Compute_RequiredRefusal_Refused_Returns1()
    {
        // Out-of-scope question, agent correctly refused.
        Assert.Equal(1.0, _evaluator.Compute(
            predictedRefusal: true, acceptableRefusal: true, refusalRequired: true));
    }

    [Fact]
    public void Compute_RequiredRefusal_Answered_Returns0()
    {
        // Out-of-scope question, agent fabricated an answer instead of
        // refusing — exactly the failure mode ADR-0017 guards against.
        Assert.Equal(0.0, _evaluator.Compute(
            predictedRefusal: false, acceptableRefusal: true, refusalRequired: true));
    }

    [Fact]
    public void Compute_AcceptableOnly_Refused_ReturnsNull()
    {
        // Content-gap row (e.g. JJP Toy Story 4): refusing is fine —
        // and carries no signal, so the row is excluded from the mean.
        Assert.Null(_evaluator.Compute(
            predictedRefusal: true, acceptableRefusal: true, refusalRequired: false));
    }

    [Fact]
    public void Compute_AcceptableOnly_Answered_ReturnsNull()
    {
        // Content-gap row answered (correctness graded by the citation
        // metrics): equally fine. The two-state evaluator scored this
        // 0.0 — the strike-one measurement artifact this fix removes.
        Assert.Null(_evaluator.Compute(
            predictedRefusal: false, acceptableRefusal: true, refusalRequired: false));
    }

    [Fact]
    public void Compute_MustAnswer_Answered_Returns1()
    {
        // Grounded question, agent correctly answered.
        Assert.Equal(1.0, _evaluator.Compute(
            predictedRefusal: false, acceptableRefusal: false, refusalRequired: false));
    }

    [Fact]
    public void Compute_MustAnswer_OverEagerRefusal_Returns0()
    {
        // Symmetric concern: the agent refused a question it should
        // have been able to answer — also a regression.
        Assert.Equal(0.0, _evaluator.Compute(
            predictedRefusal: true, acceptableRefusal: false, refusalRequired: false));
    }
}
