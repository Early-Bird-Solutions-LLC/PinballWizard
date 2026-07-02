using System.Text.Json.Serialization;

namespace PinballWizard.Application.Ai.Evaluation.Findability;

// Scored result for a single findability probe. Produced by
// FindabilityEvalRunner and collected into FindabilityEvalRunResult.
public sealed record FindabilityProbeResult(
    [property: JsonPropertyName("id")] string ProbeId,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("ranked_candidates")] IReadOnlyList<string> RankedCandidates,
    [property: JsonPropertyName("recall_at_1")] double RecallAt1,
    [property: JsonPropertyName("recall_at_k")] double RecallAtK,
    [property: JsonPropertyName("mrr")] double Mrr,
    [property: JsonPropertyName("ndcg_at_k")] double NdcgAtK);

// Aggregate across all probes in a findability evaluation run. All mean
// values are arithmetic means over per-probe scores — the same convention
// as EvalAggregate in the Wizard harness (ADR-0016). Serializes to JSON
// so results files can be committed and diffed across runs.
public sealed record FindabilityEvalRunResult(
    [property: JsonPropertyName("probe_count")] int ProbeCount,
    [property: JsonPropertyName("k")] int K,
    [property: JsonPropertyName("recall_at_1_mean")] double RecallAt1Mean,
    [property: JsonPropertyName("recall_at_k_mean")] double RecallAtKMean,
    [property: JsonPropertyName("mrr_mean")] double MrrMean,
    [property: JsonPropertyName("ndcg_at_k_mean")] double NdcgAtKMean,
    [property: JsonPropertyName("probe_results")] IReadOnlyList<FindabilityProbeResult> ProbeResults);
