using PinballWizard.Application.Ai.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Reranking;

// Null-object implementation of ICrossEncoderReranker (ADR-0024).
// Used when Rag:CrossEncoder:Enabled=false (the default). Returns the
// first topN candidates unchanged, using each chunk's existing AI Search
// score as the RelevanceScore. No external calls; completes synchronously.
public sealed class NullCrossEncoderReranker : ICrossEncoderReranker
{
    public Task<IReadOnlyList<RankedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        CancellationToken cancellationToken)
    {
        var take = Math.Min(topN, candidates.Count);
        var result = new RankedChunk[take];
        for (var i = 0; i < take; i++)
        {
            result[i] = new RankedChunk(candidates[i], RelevanceScore: (float)candidates[i].Score);
        }
        return Task.FromResult<IReadOnlyList<RankedChunk>>(result);
    }
}
