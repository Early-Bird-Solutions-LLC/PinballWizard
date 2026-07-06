using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Evaluation.Findability;
using PinballWizard.Application.Findability;

namespace PinballWizard.Infrastructure.Ai.Evaluation;

// ADR-0049 phase 2b: eval seam that wraps IMachineSearchIndex as an
// IFindabilityLookup so FindabilityEvalRunner can measure Recall@k / MRR /
// NDCG@k against the AI Search machine index.
//
// FindabilityEvalRunner is architecture-agnostic — it calls IFindabilityLookup
// without knowing whether the backend is AI Search, getMachineByTitle (Cosmos
// point-read), or a future retrieval layer. This adapter bridges the two
// contracts with minimal logic: call SearchAsync, project OPDB IDs in order.
//
// Default top=10 matches the eval harness convention (k ≤ 10 in all current
// eval runs). Callers that need a different depth can use SearchAsync directly.
public sealed class MachineSearchFindabilityLookup(
    IMachineSearchIndex machineSearchIndex,
    ILogger<MachineSearchFindabilityLookup> logger) : IFindabilityLookup
{
    private const int DefaultTop = 10;

    public async Task<IReadOnlyList<string>> GetRankedCandidatesAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var hits = await machineSearchIndex
            .SearchAsync(query, DefaultTop, manufacturerKey: null, cancellationToken)
            .ConfigureAwait(false);

        var ids = new string[hits.Count];
        for (var i = 0; i < hits.Count; i++)
            ids[i] = hits[i].OpdbId;

        logger.LogDebug(
            "MachineSearchFindabilityLookup: query='{Query}' returned {Count} ranked candidate(s).",
            query, ids.Length);

        return ids;
    }
}
