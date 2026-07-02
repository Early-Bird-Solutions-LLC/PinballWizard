namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Retrieval quality evaluator: fraction of expectedOpdbIds that appear in
// the top-k of a ranked candidate list.
//
// Recall@k = |expected ∩ top-k candidates| / |expected|
//
// For a single-machine probe (the common case), Recall@1 = 1.0 if the
// correct machine is the first result, 0.0 otherwise; Recall@k = 1.0 if
// it appears anywhere in the top-k.
//
// With multiple expected IDs (e.g. two valid editions of the same game),
// partial credit is awarded: finding one of two expected machines in the
// top-k yields 0.5.
//
// Edge cases:
//   - k > |rankedCandidates|: evaluated over all available candidates (no
//     zero-padding — the retrieval system simply returned fewer than k).
//   - expectedOpdbIds empty: 1.0 (undefined metric; nothing to recall).
//   - rankedCandidates empty, non-empty expected: 0.0.
//   - Matching is case-insensitive (OPDB IDs are ASCII but lowercase
//     variants appear in test fixtures).
//
// Pure deterministic logic; singleton-safe.
public sealed class RecallAtKEvaluator
{
    public double Compute(
        IReadOnlyList<string> rankedCandidates,
        IReadOnlyCollection<string> expectedOpdbIds,
        int k)
    {
        ArgumentNullException.ThrowIfNull(rankedCandidates);
        ArgumentNullException.ThrowIfNull(expectedOpdbIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        if (expectedOpdbIds.Count == 0)
        {
            // Nothing expected: undefined metric; return 1.0 by convention
            // (matches RecallAtKEvaluator's empty-expected semantic and
            // MrrEvaluator's empty-expected branch).
            return 1.0;
        }

        var expectedSet = new HashSet<string>(expectedOpdbIds, StringComparer.OrdinalIgnoreCase);
        var window = Math.Min(k, rankedCandidates.Count);

        // Count each expected ID at most once. A well-behaved IFindabilityLookup
        // returns distinct results, but if one repeats a candidate the metric
        // must still never exceed 1.0 — the `counted` set enforces that ceiling.
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = 0;
        for (var i = 0; i < window; i++)
        {
            var candidate = rankedCandidates[i];
            if (expectedSet.Contains(candidate) && counted.Add(candidate))
            {
                hits++;
            }
        }

        return (double)hits / expectedSet.Count;
    }
}
