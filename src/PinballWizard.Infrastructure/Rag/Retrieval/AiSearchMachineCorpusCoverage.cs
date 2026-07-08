using Azure.Search.Documents;
using PinballWizard.Application.Ai.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Retrieval;

// AI Search implementation of IMachineCorpusCoverage (ADR-0052). Issues a
// Size=0, IncludeTotalCount=true count over the corpus index scoped to
// machine_id — the same pattern CosmosAiSearchRagReconciler.CountChunksAsync
// uses — and reuses AiSearchRagRetriever.BuildFilter so the machine filter
// is provably identical to the retrieval path (see the parity contract test).
// The router (AiRouter) already logs the gate decision with machineId, so
// this class needs no logging of its own.
public sealed class AiSearchMachineCorpusCoverage : IMachineCorpusCoverage
{
    private readonly SearchClient _searchClient;

    public AiSearchMachineCorpusCoverage(SearchClient searchClient)
    {
        ArgumentNullException.ThrowIfNull(searchClient);
        _searchClient = searchClient;
    }

    public async Task<bool> HasIndexedContentAsync(string machineId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        var options = new SearchOptions
        {
            Filter = BuildCountFilter(machineId),
            IncludeTotalCount = true,
            Size = 0,
        };

        var response = await _searchClient
            .SearchAsync<RetrievedChunkDocument>(searchText: "*", options, ct)
            .ConfigureAwait(false);

        var count = response.Value.TotalCount ?? 0;
        return count > 0;
    }

    // Single-clause machine filter, delegated to the retriever's builder so
    // the coverage query and real retrieval can never diverge on filter
    // shape or OData escaping. Non-null for any non-empty machineId.
    internal static string BuildCountFilter(string machineId)
        => AiSearchRagRetriever.BuildFilter(new RetrievalOptions(MachineId: machineId))!;
}
