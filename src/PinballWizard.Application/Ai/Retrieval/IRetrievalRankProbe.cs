using PinballWizard.Application.Ai.Evaluation;

namespace PinballWizard.Application.Ai.Retrieval;

// Retrieval-rank probe (Task 2, reranker-sensitive hard eval).
// For a given EvalQuestion, runs first-stage retrieval via IRagRetriever
// and reports WHERE the gold chunk ranks, classifying it into a slice.
//
// IMPORTANT: this probe measures FIRST-STAGE rank — the raw AI Search
// hybrid+semantic order before Cohere cross-encoder reranking. The caller
// (--probe-retrieval CLI verb, Task 3) is responsible for ensuring the
// retriever runs with Rag:CrossEncoder:Enabled=false so the returned order
// is the pre-rerank order. Unit tests use a fake IRagRetriever and are
// reranker-agnostic.
public interface IRetrievalRankProbe
{
    Task<RetrievalRankResult> ProbeAsync(
        EvalQuestion question,
        int topN,
        CancellationToken cancellationToken);
}
