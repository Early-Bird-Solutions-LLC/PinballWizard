namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Retrieval quality evaluator: reciprocal rank of the first relevant result
// in a ranked candidate list.
//
// For a single probe:
//   RR = 1 / rank_of_first_expected_hit
//      = 0   when no expected machine appears in the ranked list
//
// MRR (Mean Reciprocal Rank) is the arithmetic mean of per-probe RR values,
// computed by FindabilityEvalRunner across all probes in the dataset.
//
// With multiple expected IDs, the FIRST hit in the ranked list determines
// the reciprocal rank — e.g. if A is at rank 3 and B is at rank 2, and
// both are expected, RR = 1/2 (B's rank).
//
// Edge cases:
//   - expectedOpdbIds empty: 1.0 (undefined; nothing to find).
//   - rankedCandidates empty, non-empty expected: 0.0.
//   - Matching is case-insensitive.
//
// Pure deterministic logic; singleton-safe.
public sealed class MrrEvaluator
{
    public double ComputeReciprocalRank(
        IReadOnlyList<string> rankedCandidates,
        IReadOnlyCollection<string> expectedOpdbIds)
    {
        ArgumentNullException.ThrowIfNull(rankedCandidates);
        ArgumentNullException.ThrowIfNull(expectedOpdbIds);

        if (expectedOpdbIds.Count == 0)
        {
            return 1.0;
        }

        var expectedSet = new HashSet<string>(expectedOpdbIds, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rankedCandidates.Count; i++)
        {
            if (expectedSet.Contains(rankedCandidates[i]))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0.0;
    }
}
