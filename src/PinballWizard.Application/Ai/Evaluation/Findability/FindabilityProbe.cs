using System.Text.Json.Serialization;

namespace PinballWizard.Application.Ai.Evaluation.Findability;

// One ground-truth row from a findability probe .jsonl file. Each row encodes
// a natural-language query (what a user might type) and the OPDB IDs of the
// machines that are the correct retrieval targets.
//
// An optional graded map assigns relevance scores (0–3) to OPDB IDs, enabling
// NDCG@k to distinguish highly relevant from marginally relevant results. When
// graded is absent or null, all expectedOpdbIds are treated as binary-grade-1
// (relevant) and everything else as grade 0 for NDCG computation.
//
// JSON shape (one object per line in the .jsonl file):
//   {
//     "id": "find-001",
//     "query": "Foo Fighters pinball machine rules",
//     "expected_opdb_ids": ["GRBN-MQR4P"],
//     "graded": {"GRBN-MQR4P": 3, "OTHER-ID": 1}
//   }
//
// Grade semantics (follows standard IR relevance scales):
//   3 — perfectly relevant: the canonical machine for this query
//   2 — highly relevant: a close alternate (different edition, same franchise)
//   1 — marginally relevant: mentioned or loosely related
//   0 — not relevant (default for unlisted IDs)
public sealed record FindabilityProbe(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("expected_opdb_ids")] IReadOnlyList<string> ExpectedOpdbIds,
    [property: JsonPropertyName("graded")] IReadOnlyDictionary<string, int>? Graded = null);
