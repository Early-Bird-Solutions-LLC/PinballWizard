using PinballWizard.Application.Ai.Evaluation;
using PinballWizard.Application.Ai.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Retrieval;

// Retrieval-rank probe implementation (Task 2, reranker-sensitive hard eval).
// Calls IRagRetriever.RetrieveAsync in the order the retriever returns chunks
// (first-stage rank) and finds where the gold chunk from EvalQuestion.ExpectedCitationSet
// lands, classifying the question into a slice.
//
// IMPORTANT: this probe measures first-stage (pre-rerank) order. Callers must
// ensure the retriever runs with Rag:CrossEncoder:Enabled=false so the returned
// list reflects the AI Search hybrid+semantic order, not Cohere cross-encoder order.
// The probe itself does NOT modify retrieval options related to reranking.
//
// Citation matching: a RetrievedChunk matches the gold set when ProjectCitationId(chunk)
// is in EvalQuestion.ExpectedCitationSet or any set in AcceptableCitationSets.
// The projection is the BARE OPDB MachineId (e.g. "GweeP-MW95j") — no "mch_" prefix —
// matching EvaluationHarness.ExtractCitationIds which stores citation.MachineId directly
// (EvaluationHarness.cs ~line 442). The eval ground-truth (data/eval/wizard.v2.jsonl)
// likewise stores bare ids. CitationPrecisionEvaluator.Compute uses the same
// case-insensitive set membership check applied here via HashSet<string>.
public sealed class RetrievalRankProbe(IRagRetriever retriever) : IRetrievalRankProbe
{
    // Projects a retrieved chunk to the citation id format used in EvalQuestion
    // expected citation sets. Convention: bare OPDB MachineId (no "mch_" prefix),
    // matching EvaluationHarness.ExtractCitationIds (citation.MachineId, no prefix added)
    // and data/eval/wizard.v2.jsonl ground-truth ids.
    // Shared so that any caller (probe, future harness extensions) uses the same
    // projection and cannot drift independently.
    internal static string ProjectCitationId(RetrievedChunk chunk) =>
        chunk.MachineId;

    public async Task<RetrievalRankResult> ProbeAsync(
        EvalQuestion question,
        int topN,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);

        // TopK=10 is the production default (RetrievalOptions.cs). This value defines the
        // reranker's input window: ranks 1..topN are "easy" (first-stage already surfaces
        // them), ranks topN+1..TopK are "reranker-sensitive" (reranker must promote them
        // into the top-N for the question to be answered). If production TopK changes, this
        // must change in lock-step so the slice boundary remains meaningful.
        var options = new RetrievalOptions();
        var chunks = await retriever.RetrieveAsync(question.Question, options, cancellationToken)
            .ConfigureAwait(false);

        // Build the union of all acceptable gold citation IDs (case-insensitive).
        // AcceptableCitationSets models "any of these sets is correct" (AB#259);
        // a chunk matching ANY gold id from ANY acceptable set counts.
        var goldIds = BuildGoldIdSet(question);

        // Walk chunks in retrieval order (1-based rank). Record the first rank
        // whose projected citation id appears in the gold set.
        int? goldRank = null;
        var topCitations = new List<string>(Math.Min(topN, chunks.Count));

        for (var i = 0; i < chunks.Count; i++)
        {
            var citationId = ProjectCitationId(chunks[i]);
            if (i < topN)
            {
                topCitations.Add(citationId);
            }

            if (goldRank is null && goldIds.Contains(citationId))
            {
                goldRank = i + 1; // 1-based
            }
        }

        var slice = ClassifySlice(goldRank, topN);
        return new RetrievalRankResult(goldRank, slice, topCitations);
    }

    private static HashSet<string> BuildGoldIdSet(EvalQuestion question)
    {
        var goldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in question.ExpectedCitationSet)
        {
            goldIds.Add(id);
        }

        if (question.AcceptableCitationSets is not null)
        {
            foreach (var set in question.AcceptableCitationSets)
            {
                foreach (var id in set)
                {
                    goldIds.Add(id);
                }
            }
        }

        return goldIds;
    }

    private static string ClassifySlice(int? goldRank, int topN) => goldRank switch
    {
        null => "retrieval-miss",
        var r when r <= topN => "easy",
        _ => "reranker-sensitive"
    };
}
