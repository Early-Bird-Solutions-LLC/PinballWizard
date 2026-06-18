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

    // Edition-aware any-of scoring (AB#259, edition-scope-model-design §6).
    // Scores recall against the MOST FAVORABLE acceptable set in
    // EvalQuestion.AcceptableCitationSets — symmetric with
    // CitationPrecisionEvaluator.ComputeAnyOf. For R1 ([[Pro],[Prem/LE]]),
    // citing the Pro base recalls the [Pro] set fully (1.0). For an
    // edition-subset row ([[Pro,Prem/LE]]), citing one of two → 0.5.
    //
    // Empty acceptableSets is treated as "no citation expected" → 1.0
    // (recall undefined), matching Compute's empty-expected branch.
    public double ComputeAnyOf(
        IReadOnlyCollection<string> predicted,
        IReadOnlyList<IReadOnlyList<string>> acceptableSets)
    {
        ArgumentNullException.ThrowIfNull(predicted);
        ArgumentNullException.ThrowIfNull(acceptableSets);

        if (acceptableSets.Count == 0)
        {
            return Compute(predicted, []);
        }

        var best = 0.0;
        foreach (var set in acceptableSets)
        {
            var score = Compute(predicted, set ?? []);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }
}
