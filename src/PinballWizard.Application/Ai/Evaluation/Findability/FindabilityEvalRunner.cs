using PinballWizard.Application.Ai.Evaluation.Evaluators;

namespace PinballWizard.Application.Ai.Evaluation.Findability;

// Orchestrates a findability evaluation run: drives each probe through the
// IFindabilityLookup, computes Recall@1, Recall@k, MRR, and NDCG@k per
// probe, then aggregates arithmetic means across all probes.
//
// Architecture-agnostic: the lookup backend (getMachineByTitle, AI Search,
// fuzzy string match, etc.) is injected via IFindabilityLookup. This lets
// offline eval infrastructure exist before any particular Phase 2 change
// ships and allows A/B comparisons between lookup backends by swapping the
// injected implementation.
//
// Probe-level NDCG uses graded relevance when the probe carries a graded
// map; falls back to binary relevance (grade 1 for each expected ID)
// otherwise. This means a .jsonl file of ungraded probes measures binary
// findability (is the correct machine retrieved?), while a graded file
// measures ranking quality (is the most relevant machine ranked highest?).
public sealed class FindabilityEvalRunner(
    IFindabilityLookup lookup,
    RecallAtKEvaluator recallEvaluator,
    MrrEvaluator mrrEvaluator,
    NdcgAtKEvaluator ndcgEvaluator)
{
    public async Task<FindabilityEvalRunResult> RunAsync(
        IReadOnlyList<FindabilityProbe> probes,
        int k,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        var probeResults = new List<FindabilityProbeResult>(probes.Count);

        foreach (var probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = await lookup.GetRankedCandidatesAsync(probe.Query, cancellationToken);

            var recallAt1 = recallEvaluator.Compute(candidates, probe.ExpectedOpdbIds, k: 1);
            var recallAtK = recallEvaluator.Compute(candidates, probe.ExpectedOpdbIds, k);
            var mrr = mrrEvaluator.ComputeReciprocalRank(candidates, probe.ExpectedOpdbIds);

            var ndcgAtK = probe.Graded is { Count: > 0 }
                ? ndcgEvaluator.Compute(candidates, probe.Graded, k)
                : ndcgEvaluator.Compute(candidates, probe.ExpectedOpdbIds, k);

            probeResults.Add(new FindabilityProbeResult(
                ProbeId: probe.Id,
                Query: probe.Query,
                RankedCandidates: candidates,
                RecallAt1: recallAt1,
                RecallAtK: recallAtK,
                Mrr: mrr,
                NdcgAtK: ndcgAtK));
        }

        var count = probeResults.Count;

        return new FindabilityEvalRunResult(
            ProbeCount: count,
            K: k,
            RecallAt1Mean: count == 0 ? 0.0 : probeResults.Average(r => r.RecallAt1),
            RecallAtKMean: count == 0 ? 0.0 : probeResults.Average(r => r.RecallAtK),
            MrrMean: count == 0 ? 0.0 : probeResults.Average(r => r.Mrr),
            NdcgAtKMean: count == 0 ? 0.0 : probeResults.Average(r => r.NdcgAtK),
            ProbeResults: probeResults);
    }
}
