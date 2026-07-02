namespace PinballWizard.Application.Ai.Evaluation.Evaluators;

// Retrieval quality evaluator: Normalized Discounted Cumulative Gain at k.
//
// Standard IR formula (graded version):
//   DCG@k  = Σ_{i=1}^{k}  (2^rel_i − 1) / log₂(i+1)
//   IDCG@k = DCG@k of the ideal (highest-grade-first) ranking
//   NDCG@k = DCG@k / IDCG@k
//
// where rel_i is the relevance grade (0–3) of the item ranked at position i,
// and log₂(i+1) is the positional discount (position 1 → log₂(2) = 1.0).
//
// Binary convenience overload: supply expectedOpdbIds with no grade map;
// each expected ID is treated as grade 1, all other IDs as grade 0.
// Binary NDCG@k degenerates correctly to set-recall-weighted gain:
//   IDCG@k = Σ_{i=1}^{min(|expected|,k)} 1/log₂(i+1)
//
// Edge cases:
//   - IDCG = 0 (no relevant items at all in the grades map): returns 1.0.
//     Convention matches the empty-expected branches in RecallAtKEvaluator
//     and MrrEvaluator — the metric is undefined when there is nothing to
//     find, so the probe is not penalized.
//   - k > |rankedCandidates|: evaluated over all available candidates.
//   - Grade values < 0 are clamped to 0 (negative relevance is not a
//     standard IR concept; callers should validate inputs upstream).
//   - Matching is case-insensitive.
//
// Pure deterministic logic; singleton-safe.
public sealed class NdcgAtKEvaluator
{
    // Graded overload: explicit relevance grade per OPDB ID.
    public double Compute(
        IReadOnlyList<string> rankedCandidates,
        IReadOnlyDictionary<string, int> grades,
        int k)
    {
        ArgumentNullException.ThrowIfNull(rankedCandidates);
        ArgumentNullException.ThrowIfNull(grades);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        // Build a case-insensitive grade lookup to avoid callers having
        // to normalize OPDB ID casing before calling.
        var gradeLookup = new Dictionary<string, int>(grades, StringComparer.OrdinalIgnoreCase);

        var idcg = ComputeIdcg(gradeLookup.Values, k);
        if (idcg == 0.0)
        {
            return 1.0;
        }

        var dcg = ComputeDcg(rankedCandidates, gradeLookup, k);
        return dcg / idcg;
    }

    // Binary overload: expectedOpdbIds → grade 1; everything else → grade 0.
    public double Compute(
        IReadOnlyList<string> rankedCandidates,
        IReadOnlyCollection<string> expectedOpdbIds,
        int k)
    {
        ArgumentNullException.ThrowIfNull(rankedCandidates);
        ArgumentNullException.ThrowIfNull(expectedOpdbIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        // Distinct to guard against duplicate IDs in a probe's expected list
        // (the parser does not enforce intra-probe uniqueness).
        var grades = expectedOpdbIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, _ => 1, StringComparer.OrdinalIgnoreCase);

        return Compute(rankedCandidates, grades, k);
    }

    private static double ComputeDcg(
        IReadOnlyList<string> rankedCandidates,
        Dictionary<string, int> gradeLookup,
        int k)
    {
        var dcg = 0.0;
        var window = Math.Min(k, rankedCandidates.Count);

        for (var i = 0; i < window; i++)
        {
            gradeLookup.TryGetValue(rankedCandidates[i], out var rel);
            var clampedRel = Math.Max(0, rel);
            // Position is 1-based: i=0 → position 1, discount = log₂(2) = 1.0
            dcg += (Math.Pow(2.0, clampedRel) - 1.0) / Math.Log2(i + 2);
        }

        return dcg;
    }

    private static double ComputeIdcg(IEnumerable<int> allGrades, int k)
    {
        // Ideal ordering: highest grades first. Take only the top-k most
        // relevant items (the rest are outside the evaluation cutoff).
        var sorted = allGrades
            .Where(g => g > 0)
            .OrderByDescending(g => g)
            .Take(k)
            .ToList();

        var idcg = 0.0;
        for (var i = 0; i < sorted.Count; i++)
        {
            idcg += (Math.Pow(2.0, sorted[i]) - 1.0) / Math.Log2(i + 2);
        }

        return idcg;
    }
}
