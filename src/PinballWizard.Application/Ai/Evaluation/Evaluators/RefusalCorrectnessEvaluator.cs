namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: did the agent refuse when it should
// have, and not refuse when it shouldn't? An agreement score —
//   - acceptable_refusal=true,  predicted_refusal=true  → 1.0 (correct refusal)
//   - acceptable_refusal=false, predicted_refusal=false → 1.0 (correct answer)
//   - acceptable_refusal=true,  predicted_refusal=false → 0.0 (over-eager answer)
//   - acceptable_refusal=false, predicted_refusal=true  → 0.0 (over-eager refusal)
//
// Per ADR-0017, refusal is a feature, not a failure: the harness
// rewards the agent for staying within its grounded domain (out-of-
// scope questions answered with "I don't know" score 1.0). This
// metric guards the symmetric concern — an over-eager refusal on a
// question we *can* answer is also a regression.
public sealed class RefusalCorrectnessEvaluator
{
    public const string EvaluatorName = "refusal_correctness";

    public double Compute(bool predictedRefusal, bool acceptableRefusal)
    {
        return predictedRefusal == acceptableRefusal ? 1.0 : 0.0;
    }
}
