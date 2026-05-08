namespace PinballWizard.Application.Ai.Retrieval;

// Phase 4 RAG retrieval abstraction (build-spec § Phase 4 item 20).
// Implementations execute a hybrid (vector + keyword + semantic)
// query against the AI Search index defined by ADR-0021 and return
// the page-anchored chunks the Wizard's `searchCorpus` tool surface
// (item 21, W4-1) cites in answers. The default implementation is
// `Infrastructure.Rag.Retrieval.AiSearchRagRetriever`.
//
// Returns an empty list (not null, no exception) when the index is
// empty, the query returns zero hits, or every hit falls below
// `RetrievalOptions.MinimumScore`. The orchestrator distinguishes
// these cases from retrieval-side errors by the absence of an
// exception — empty result is a valid retrieval outcome that the
// citation-required guardrail (ADR-0023) interprets as
// "refuse rather than fabricate".
public interface IRagRetriever
{
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string queryText,
        RetrievalOptions options,
        CancellationToken cancellationToken);
}
