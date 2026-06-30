namespace PinballWizard.Application.Ai.Retrieval;

// Result of IRetrievalRankProbe.ProbeAsync. Carries the 1-based rank of the
// gold chunk in the first-stage retrieval list (null when not retrieved), the
// derived slice classification, and the citation ids of the top-retrieved chunks
// for diagnostic output by the CLI verb (Task 3).
//
// Slice values:
//   "easy"               — GoldRank <= topN (already surfaced to the agent)
//   "reranker-sensitive" — GoldRank in (topN, topK] (retrieved but buried;
//                          reranker lift is the hypothesis)
//   "retrieval-miss"     — GoldRank null (not in the retrieved set at all)
public sealed record RetrievalRankResult(
    int? GoldRank,
    string Slice,
    IReadOnlyList<string> TopChunkCitations);
