namespace PinballWizard.Application.Ai.Retrieval;

// Single source of truth for converting a raw Azure AI Search relevance
// score into a normalized 0–1 fraction. Both the retriever's minimum-score
// floor (Infrastructure) and the citation "% match" badge (Web) MUST use
// this helper — a divergent per-layer constant is exactly the 0–4-vs-0–1
// scale bug this type exists to prevent (see
// docs/superpowers/specs/2026-07-06-rag-relevance-floor-and-machine-scope-design.md).
// The semantic reranker (@search.rerankerScore) is documented 0.0–4.0;
// BM25-fallback scores are unbounded, so the result is clamped to [0,1].
public static class RetrievalScoring
{
    // Azure AI Search semantic reranker ceiling (@search.rerankerScore max).
    public const double MaxRerankerScore = 4.0;

    // Normalize a raw relevance score to a 0–1 fraction of the reranker
    // ceiling, clamped. The value equals the citation card's "% match" / 100.
    public static double NormalizeRerankerScore(double rawScore) =>
        Math.Clamp(rawScore / MaxRerankerScore, 0.0, 1.0);
}
