namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: fraction of expected citations that
// appear in the predicted set. The complement of precision —
// precision asks "is what we cited correct?", recall asks "did we
// cite everything we should have?". A grounded answer that drops
// half its expected citations still scores 1.0 on precision but 0.5
// on recall — both signals are needed to detect the failure modes
// ADR-0016 cares about.
//
// Conventions match CitationPrecisionEvaluator's set semantics:
//   - Empty expected set, empty predicted: 1.0 (refusal honored).
//   - Empty expected set, non-empty predicted: 1.0 (no expected
//     citation to recall — recall is undefined; we choose 1.0 so the
//     refusal-flow doesn't double-penalize against the precision = 0
//     case where the agent hallucinated a citation).
//   - Non-empty expected, empty predicted: 0.0 (no recalled citations).
//   - Both non-empty: |expected ∩ predicted| / |expected|.
public sealed class CitationRecallEvaluator
{
    public const string EvaluatorName = "citation_recall";

    public double Compute(
        IReadOnlyCollection<string> predicted,
        IReadOnlyCollection<string> expected)
    {
        ArgumentNullException.ThrowIfNull(predicted);
        ArgumentNullException.ThrowIfNull(expected);

        var expectedSet = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        var predictedSet = new HashSet<string>(predicted, StringComparer.OrdinalIgnoreCase);

        if (expectedSet.Count == 0)
        {
            // No expected citations: recall is undefined. Return 1.0
            // (the refusal-honored case); precision penalizes the
            // hallucination case independently.
            return 1.0;
        }

        var hits = 0;
        foreach (var expectedId in expectedSet)
        {
            if (predictedSet.Contains(expectedId))
            {
                hits++;
            }
        }

        return (double)hits / expectedSet.Count;
    }
}
