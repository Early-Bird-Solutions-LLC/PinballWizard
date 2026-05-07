namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Custom code-based evaluator: fraction of predicted citations that
// appear in the expected citation set. The load-bearing showcase
// metric per ADR-0016 § Decision (precision is what guards against
// the answer-fabrication failure mode that provenance-as-sacred —
// guardrails.md goal #5 — is built to prevent).
//
// Conventions:
//   - Both inputs are case-insensitive sets keyed by the OPDB id (or
//     a future doc_<chunk> id). The harness extracts predicted ids
//     from the WizardAnswer's Citations[].MachineId (and DocumentChunkId
//     when Phase 4 lands).
//   - Empty predicted set with non-empty expected: precision = 0.0
//     (no signal that we cited anything, but a citation was expected).
//   - Empty expected set (acceptable_refusal questions): precision = 1.0
//     when the predicted set is also empty (no false-positive citation
//     against an out-of-scope question), 0.0 otherwise (the agent
//     hallucinated a citation when none was warranted).
//   - Both empty: 1.0 (refusal flow honored).
//
// The class is a singleton; Compute is pure — no I/O, no shared state.
// Foundry registers this evaluator with a Python equivalent for portal
// surface alignment (see EvaluationHarness.RegisterEvaluatorsAsync).
public sealed class CitationPrecisionEvaluator
{
    public const string EvaluatorName = "citation_precision";

    public double Compute(
        IReadOnlyCollection<string> predicted,
        IReadOnlyCollection<string> expected)
    {
        ArgumentNullException.ThrowIfNull(predicted);
        ArgumentNullException.ThrowIfNull(expected);

        var expectedSet = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        var predictedSet = new HashSet<string>(predicted, StringComparer.OrdinalIgnoreCase);

        if (predictedSet.Count == 0)
        {
            // No predicted citations: precision is 1.0 only when no
            // citation was expected (refusal-honored), 0.0 otherwise.
            return expectedSet.Count == 0 ? 1.0 : 0.0;
        }

        var hits = 0;
        foreach (var predictedId in predictedSet)
        {
            if (expectedSet.Contains(predictedId))
            {
                hits++;
            }
        }

        return (double)hits / predictedSet.Count;
    }
}
