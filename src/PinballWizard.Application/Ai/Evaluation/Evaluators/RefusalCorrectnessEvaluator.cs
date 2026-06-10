namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: did the agent refuse when it had to,
// and answer when it had to? Three-state per the AB#259 metric-hygiene
// fix (2026-06-10) — the original two-state form scored acceptable_refusal
// as REQUIRED-refusal, so a correct grounded answer on a content-gap row
// (JJP Toy Story 4) scored 0:
//
//   - refusal_required=true,  predicted_refusal=true  → 1.0 (correct refusal)
//   - refusal_required=true,  predicted_refusal=false → 0.0 (over-eager answer)
//   - acceptable_refusal only (gap row), either way    → null (both behaviors
//     are correct — the row carries no refusal signal and is excluded from
//     the aggregate denominator, mirroring the R2/R3 nullable pattern)
//   - neither flag,           predicted_refusal=false → 1.0 (correct answer)
//   - neither flag,           predicted_refusal=true  → 0.0 (over-eager refusal)
//
// Per ADR-0017, refusal is a feature, not a failure: the harness
// rewards the agent for staying within its grounded domain. This
// metric guards both edges — fabricating an answer to an out-of-scope
// question, and refusing a question the corpus can answer.
public sealed class RefusalCorrectnessEvaluator
{
    public const string EvaluatorName = "refusal_correctness";

    public double? Compute(bool predictedRefusal, bool acceptableRefusal, bool refusalRequired)
    {
        if (refusalRequired)
        {
            return predictedRefusal ? 1.0 : 0.0;
        }

        if (acceptableRefusal)
        {
            // Content-gap row: refusing and answering are both correct,
            // so the row contributes no refusal signal either way.
            return null;
        }

        return predictedRefusal ? 0.0 : 1.0;
    }
}
