using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Rag.Reranking;

// Cohere Rerank implementation of ICrossEncoderReranker (ADR-0024, amended
// to an Azure-native Foundry MaaS deployment). Calls the Foundry account's
// native Cohere rerank route (/providers/cohere/v2/rerank) keyless via the
// managed identity. POSTs the Cohere v2 rerank JSON body and reads a JSON
// response with a "results" array of "index" and "relevance_score" fields.
//
// The HttpClient is injected by DI (registered in ServiceCollectionExtensions
// with the Foundry AAD bearer token via DefaultAzureCredential). The
// client's BaseAddress is NOT set here — the full ModelEndpoint URL from
// CrossEncoderOptions is used on each call so the endpoint is visible in
// logs and can be overridden per environment without recreating the client.
public sealed class CohereRerankReranker : ICrossEncoderReranker
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly CrossEncoderOptions _options;
    private readonly ILogger<CohereRerankReranker> _logger;

    public CohereRerankReranker(
        HttpClient httpClient,
        IOptions<CrossEncoderOptions> options,
        ILogger<CohereRerankReranker> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RankedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return [];

        var requestBody = new CohereRerankRequest(
            Model: _options.ModelId,
            Query: query,
            Documents: candidates.Select(c => new CohereDocument(c.Content)).ToArray(),
            TopN: Math.Min(topN, candidates.Count));

        _logger.LogInformation(
            "Cohere rerank: query_length={QueryLength}, candidates={CandidateCount}, topN={TopN}.",
            query.Length, candidates.Count, requestBody.TopN);

        var response = await _httpClient
            .PostAsJsonAsync(_options.ModelEndpoint, requestBody, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var cohereResponse = await response.Content
            .ReadFromJsonAsync<CohereRerankResponse>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cohere rerank response was null.");

        var effectiveTopN = Math.Min(topN, candidates.Count);
        var ranked = new List<RankedChunk>(capacity: effectiveTopN);
        foreach (var result in cohereResponse.Results)
        {
            if (ranked.Count >= effectiveTopN)
                break;

            if (result.Index < 0 || result.Index >= candidates.Count)
            {
                _logger.LogWarning(
                    "Cohere rerank returned out-of-range index {Index} (candidates={Count}); skipping.",
                    result.Index, candidates.Count);
                continue;
            }
            ranked.Add(new RankedChunk(candidates[result.Index], RelevanceScore: (float)result.RelevanceScore));
        }

        return ranked;
    }

    // Request shape matching the Cohere Rerank v2 API / Foundry Cohere connection.
    private sealed record CohereRerankRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] CohereDocument[] Documents,
        [property: JsonPropertyName("top_n")] int TopN);

    private sealed record CohereDocument(
        [property: JsonPropertyName("text")] string Text);

    private sealed record CohereRerankResponse(
        [property: JsonPropertyName("results")] CohereRerankResult[] Results);

    private sealed record CohereRerankResult(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}
