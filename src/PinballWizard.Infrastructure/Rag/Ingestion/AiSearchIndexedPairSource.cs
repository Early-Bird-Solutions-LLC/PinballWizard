using System.Runtime.CompilerServices;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Infrastructure.Rag.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Default `IIndexedPairSource`. Enumerates the RAG search index and
// yields each distinct (document_id, machine_id) pair exactly once.
//
// Projects only document_id + machine_id (never the 3072-d embedding or
// the content body) to keep the scan cheap, and de-duplicates in memory
// with a HashSet. No Size cap: GetResultsAsync follows AI Search's
// continuation tokens so every chunk is visited — a Size limit would
// silently miss pairs beyond the first page and make the garbage
// collector under-report orphans (invariant #17: no silent caps).
//
// This is an admin/maintenance read (driven by the --gc-rag-index CLI
// verb), not a hot path — the whole-index scan is acceptable because it
// runs on demand, not per request.
public sealed class AiSearchIndexedPairSource : IIndexedPairSource
{
    private readonly SearchClient _searchClient;
    private readonly ILogger<AiSearchIndexedPairSource> _logger;

    public AiSearchIndexedPairSource(
        SearchClient searchClient,
        ILogger<AiSearchIndexedPairSource> logger)
    {
        ArgumentNullException.ThrowIfNull(searchClient);
        ArgumentNullException.ThrowIfNull(logger);
        _searchClient = searchClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<IndexedPair> StreamIndexedPairsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = new SearchOptions();
        options.Select.Add(AiSearchIndexFields.DocumentId);
        options.Select.Add(AiSearchIndexFields.MachineId);

        var response = await _searchClient
            .SearchAsync<SearchDocument>(searchText: "*", options, cancellationToken)
            .ConfigureAwait(false);

        var seen = new HashSet<(string, string)>();
        var chunks = 0;
        await foreach (var result in response.Value.GetResultsAsync().ConfigureAwait(false))
        {
            chunks++;
            if (!result.Document.TryGetValue(AiSearchIndexFields.DocumentId, out var docRaw)
                || docRaw is not string documentId
                || string.IsNullOrEmpty(documentId))
            {
                continue;
            }
            if (!result.Document.TryGetValue(AiSearchIndexFields.MachineId, out var mchRaw)
                || mchRaw is not string machineId
                || string.IsNullOrEmpty(machineId))
            {
                continue;
            }

            if (seen.Add((documentId, machineId)))
            {
                yield return new IndexedPair(documentId, machineId);
            }
        }

        _logger.LogInformation(
            "RAG index pair scan: {DistinctPairs} distinct (document, machine) pairs across {ChunkCount} chunks.",
            seen.Count, chunks);
    }
}
