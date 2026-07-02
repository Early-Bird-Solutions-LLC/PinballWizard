namespace PinballWizard.Application.Ai.Evaluation.Findability;

// Abstraction over the retrieval backend used during findability evaluation.
// Decouples FindabilityEvalRunner from any specific lookup implementation
// (getMachineByTitle, AI Search, fuzzy string match, etc.) so offline
// evaluation infrastructure is available before the Phase 2 AI Search
// changes ship and can be driven by any future retrieval implementation.
//
// Return value: OPDB IDs ordered by decreasing relevance. An empty list
// means no candidates were found for the query. Callers must not assume
// the list is bounded — evaluators truncate at depth k themselves.
public interface IFindabilityLookup
{
    Task<IReadOnlyList<string>> GetRankedCandidatesAsync(
        string query,
        CancellationToken cancellationToken);
}
