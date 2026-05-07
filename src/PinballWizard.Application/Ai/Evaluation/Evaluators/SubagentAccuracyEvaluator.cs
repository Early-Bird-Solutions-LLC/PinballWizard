namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: did the orchestrator route the question
// to the expected sub-agent? Binary outcome — 1.0 on match, 0.0 on
// mismatch. Comparison is case-insensitive against AgentName constants
// (Wizard / Valuation / Rules / Repair).
//
// Phase 3 limitation per AiRouter.cs: the SubAgentUsed field on
// WizardAnswer is currently always "Wizard" because the per-question
// connected-agent trace correlation isn't wired yet. The evaluator
// is correct in isolation; H2 hand-off and a future PR will populate
// the actual sub-agent identity.
public sealed class SubagentAccuracyEvaluator
{
    public const string EvaluatorName = "subagent_accuracy";

    public double Compute(string predictedSubAgent, string expectedSubAgent)
    {
        ArgumentNullException.ThrowIfNull(predictedSubAgent);
        ArgumentNullException.ThrowIfNull(expectedSubAgent);

        return string.Equals(
            predictedSubAgent.Trim(),
            expectedSubAgent.Trim(),
            StringComparison.OrdinalIgnoreCase)
            ? 1.0
            : 0.0;
    }
}
