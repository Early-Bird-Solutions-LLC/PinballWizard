using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Credentials;
using PinballWizard.Infrastructure.Rag.Retrieval;

namespace PinballWizard.Infrastructure.Integrations.AiSearch;

// Reads corpus-level stats from the live Azure AI Search index. Mirrors
// AzureAiSearchSmokeProbe: owns its SearchClient (built from AiSearchOptions +
// the shared DefaultAzureCredential), validates the endpoint before any wire call,
// and uses only the data-plane read surface ("Search Index Data Reader" role). No
// Foundry dependency. An unconfigured/unreachable index throws — the page renders a
// visible alert (Invariant #17), never zeros.
public sealed class AiSearchRagCorpusStatsReader : IRagCorpusStatsReader
{
    private readonly AiSearchOptions _options;
    private readonly ILogger<AiSearchRagCorpusStatsReader> _logger;

    public AiSearchRagCorpusStatsReader(
        IOptions<AiSearchOptions> options,
        ILogger<AiSearchRagCorpusStatsReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RagCorpusStats> GetCorpusStatsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new InvalidOperationException(
                $"RAG corpus stats unavailable: {AiSearchOptions.EndpointKey} is not configured.");
        }
        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                $"RAG corpus stats unavailable: {AiSearchOptions.EndpointKey} '{_options.Endpoint}' " +
                "is not a valid absolute URL.");
        }

        var client = new SearchClient(endpoint, _options.IndexName, SharedAzureCredential.Instance);

        // 1. Total indexed chunks — single count call, no query.
        var total = await client.GetDocumentCountAsync(cancellationToken).ConfigureAwait(false);

        // 2. Chunks by document type — a faceted count (Size=0 returns no documents).
        // count:20 — document_type is a small closed set (~6-8 types); 20 is a comfortable ceiling, no truncation.
        var facetResults = await client.SearchAsync<object>(
            "*",
            new SearchOptions { Size = 0, Facets = { $"{AiSearchIndexFields.DocumentType},count:20" } },
            cancellationToken).ConfigureAwait(false);

        var byType = new List<DocTypeChunkCount>();
        if (facetResults.Value.Facets is { } facets &&
            facets.TryGetValue(AiSearchIndexFields.DocumentType, out var typeFacets))
        {
            foreach (var f in typeFacets.OrderByDescending(x => x.Count ?? 0))
            {
                byType.Add(new DocTypeChunkCount(f.Value?.ToString() ?? "(unknown)", f.Count ?? 0));
            }
        }

        // 3. Index freshness — the most recent last_scraped_utc in the index (Size=1, sorted).
        DateTimeOffset? mostRecent = null;
        var freshResults = await client.SearchAsync<RetrievedChunkDocument>(
            "*",
            new SearchOptions
            {
                Size = 1,
                OrderBy = { $"{AiSearchIndexFields.LastScrapedUtc} desc" },
                Select = { AiSearchIndexFields.LastScrapedUtc },
            },
            cancellationToken).ConfigureAwait(false);

        await foreach (var hit in freshResults.Value.GetResultsAsync().ConfigureAwait(false))
        {
            mostRecent = hit.Document.LastScrapedUtc;
            break;
        }

        _logger.LogDebug(
            "RAG corpus stats read: total={TotalChunks} docTypes={DocTypeCount} mostRecent={MostRecentScrapeUtc}",
            total.Value, byType.Count, mostRecent);

        return new RagCorpusStats(total.Value, byType, mostRecent);
    }
}
