namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: did the orchestrator route the question
// to the expected sub-agent? Binary outcome — 1.0 on match, 0.0 on
// mismatch. Comparison is case-insensitive against AgentName constants
// (Wizard / Valuation / Rules / Repair).
//
// Acceptable-sub-agents extension (AB#259): when a ground-truth row
// carries an acceptable_sub_agents list, a predicted value that matches
// any listed name also scores 1.0. The default overload (no list) is
// unchanged — exact-match against expected only. This lets curators
// annotate questions where a direct Wizard answer via getMachineByTitle
// is a correct, efficient path rather than a routing failure.
public sealed class SubagentAccuracyEvaluator
{
    public const string EvaluatorName = "subagent_accuracy";

    // Exact-match path — default behavior, no annotation on the ground-truth row.
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

    // Any-of path — used when the ground-truth row supplies acceptable_sub_agents.
    // Scores 1.0 if predicted matches expected OR is in the acceptable list.
    // acceptableSubAgents null or empty degrades to the exact-match path so
    // callers can pass the field value directly without a null guard.
    public double Compute(
        string predictedSubAgent,
        string expectedSubAgent,
        IReadOnlyList<string>? acceptableSubAgents)
    {
        ArgumentNullException.ThrowIfNull(predictedSubAgent);
        ArgumentNullException.ThrowIfNull(expectedSubAgent);

        if (acceptableSubAgents is null || acceptableSubAgents.Count == 0)
        {
            return Compute(predictedSubAgent, expectedSubAgent);
        }

        var trimmedPredicted = predictedSubAgent.Trim();

        if (string.Equals(trimmedPredicted, expectedSubAgent.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        foreach (var acceptable in acceptableSubAgents)
        {
            if (string.Equals(trimmedPredicted, acceptable.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }
        }

        return 0.0;
    }
}
