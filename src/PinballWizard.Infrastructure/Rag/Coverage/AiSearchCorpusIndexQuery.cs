using Azure.Search.Documents;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Coverage;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Credentials;
using PinballWizard.Infrastructure.Rag.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Coverage;

// ICorpusIndexQuery over Azure AI Search. Builds its SearchClient inline from
// AiSearchOptions + SharedAzureCredential, mirroring AiSearchRagCorpusStatsReader.
// Translates a RagSource recognizer into an OData filter (manufacturer value(s)
// AND/OR document_id prefix); document_id is filterable via a range comparison,
// so a per-source facet on document_type yields that source's live cells.
public sealed class AiSearchCorpusIndexQuery : ICorpusIndexQuery
{
    private readonly AiSearchOptions _options;
    private readonly Lazy<SearchClient> _client;

    public AiSearchCorpusIndexQuery(IOptions<AiSearchOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _client = new Lazy<SearchClient>(() =>
        {
            if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint))
            {
                throw new InvalidOperationException(
                    $"Corpus coverage unavailable: {AiSearchOptions.EndpointKey} '{_options.Endpoint}' is not a valid absolute URL.");
            }
            return new SearchClient(endpoint, _options.IndexName, SharedAzureCredential.Instance);
        });
    }

    public async Task<long> CountAsync(RagSource source, CancellationToken ct)
    {
        var response = await _client.Value.SearchAsync<RetrievedChunkDocument>(
            "*",
            new SearchOptions { Filter = BuildSourceFilter(source), IncludeTotalCount = true, Size = 0 },
            ct).ConfigureAwait(false);
        return response.Value.TotalCount ?? 0;
    }

    public async Task<IReadOnlyList<DocTypeCount>> FacetDocumentTypesAsync(RagSource source, CancellationToken ct)
    {
        var response = await _client.Value.SearchAsync<object>(
            "*",
            new SearchOptions
            {
                Filter = BuildSourceFilter(source),
                Size = 0,
                Facets = { $"{AiSearchIndexFields.DocumentType},count:30" },
            },
            ct).ConfigureAwait(false);

        var result = new List<DocTypeCount>();
        if (response.Value.Facets is { } facets &&
            facets.TryGetValue(AiSearchIndexFields.DocumentType, out var typeFacets))
        {
            foreach (var f in typeFacets)
            {
                var value = f.Value?.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(new DocTypeCount(value, f.Count ?? 0));
                }
            }
        }
        return result;
    }

    public async Task<CorpusSample?> SampleAsync(RagSource source, string documentType, CancellationToken ct)
    {
        var filter = $"{BuildSourceFilter(source)} and {AiSearchIndexFields.DocumentType} eq '{Escape(documentType)}'";
        var response = await _client.Value.SearchAsync<RetrievedChunkDocument>(
            "*",
            new SearchOptions
            {
                Filter = filter,
                Size = 1,
                Select =
                {
                    AiSearchIndexFields.DocumentId, AiSearchIndexFields.Manufacturer,
                    AiSearchIndexFields.DocumentType, AiSearchIndexFields.MachineTitle,
                    AiSearchIndexFields.SectionHeading,
                },
            },
            ct).ConfigureAwait(false);

        await foreach (var hit in response.Value.GetResultsAsync().ConfigureAwait(false))
        {
            var d = hit.Document;
            return new CorpusSample(d.DocumentId, d.Manufacturer, d.DocumentType, d.MachineTitle, d.SectionHeading);
        }
        return null;
    }

    // Recognizer → OData. manufacturer value(s) via equality OR-group; document_id
    // prefix via range (ge/lt). At least one clause is always present — a source
    // with neither is a catalog-configuration error and is caught here at call time.
    internal static string BuildSourceFilter(RagSource source)
    {
        var clauses = new List<string>(2);

        if (source.ManufacturerValues.Count > 0)
        {
            var ors = source.ManufacturerValues
                .Select(m => $"{AiSearchIndexFields.Manufacturer} eq '{Escape(m)}'");
            clauses.Add($"({string.Join(" or ", ors)})");
        }

        if (source.DocumentIdPrefix is { } prefix)
        {
            clauses.Add(
                $"({AiSearchIndexFields.DocumentId} ge '{Escape(prefix)}' and " +
                $"{AiSearchIndexFields.DocumentId} lt '{Escape(PrefixUpperBound(prefix))}')");
        }

        if (clauses.Count == 0)
        {
            throw new InvalidOperationException(
                $"RagSource '{source.SourceId}' has no recognizer (no manufacturer values and no document_id prefix).");
        }

        return string.Join(" and ", clauses);
    }

    // Azure AI Search $filter has no startswith(); a prefix match is expressed as a
    // range: id ge 'prefix' and id lt 'prefixUpperBound', where the upper bound is the
    // prefix with its final char incremented by one code unit (e.g. "doc_" -> "doc`").
    private static string PrefixUpperBound(string prefix)
    {
        // All recognizer prefixes are non-empty; the caller only passes non-null prefixes.
        var chars = prefix.ToCharArray();
        chars[^1] = (char)(chars[^1] + 1);
        return new string(chars);
    }

    private static string Escape(string value) =>
        value.Contains('\'', StringComparison.Ordinal)
            ? value.Replace("'", "''", StringComparison.Ordinal)
            : value;
}
