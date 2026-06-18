using System.Diagnostics;
using System.Text;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Rag.Retrieval;

// Hybrid (vector + keyword + semantic) retrieval against the
// `pinwiz-rag-v1` index defined by ADR-0021. Embeds the query text
// via `text-embedding-3-large` (ADR-0020) into a 3072-d vector,
// composes the vector + keyword + semantic-rerank query, applies
// optional facet filters from `RetrievalOptions`, and projects the
// matching index rows into `RetrievedChunk`.
//
// The score returned on each chunk is the semantic re-ranker score
// when AI Search's semantic ranker engaged, otherwise the BM25
// score. The orchestrator (W4-1, item 21) feeds these scores into
// the confidence calculator (ADR-0017) and the citation-required
// guardrail (ADR-0023). Empty result lists are a valid outcome —
// callers refuse rather than fabricating an answer.
public sealed class AiSearchRagRetriever : IRagRetriever
{
    private readonly SearchClient _searchClient;
    private readonly IQueryEmbedder _queryEmbedder;
    private readonly AiSearchOptions _aiSearchOptions;
    private readonly CrossEncoderOptions _crossEncoderOptions;
    private readonly ICrossEncoderReranker _reranker;
    private readonly ILogger<AiSearchRagRetriever> _logger;

    public AiSearchRagRetriever(
        SearchClient searchClient,
        IQueryEmbedder queryEmbedder,
        IOptions<AiSearchOptions> options,
        IOptions<CrossEncoderOptions> crossEncoderOptions,
        ICrossEncoderReranker reranker,
        ILogger<AiSearchRagRetriever> logger)
    {
        ArgumentNullException.ThrowIfNull(searchClient);
        ArgumentNullException.ThrowIfNull(queryEmbedder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(crossEncoderOptions);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(logger);
        _searchClient = searchClient;
        _queryEmbedder = queryEmbedder;
        _aiSearchOptions = options.Value;
        _crossEncoderOptions = crossEncoderOptions.Value;
        _reranker = reranker;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string queryText,
        RetrievalOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        ArgumentNullException.ThrowIfNull(options);

        // Stopwatch wraps embed + AI Search + result mapping so the
        // histogram captures user-felt retrieval latency. Emitted in
        // `finally` so cancellation and transport failures still surface
        // a duration sample.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var queryVector = await _queryEmbedder
                .EmbedAsync(queryText, cancellationToken)
                .ConfigureAwait(false);

            var searchOptions = BuildSearchOptions(queryVector, options);

            var response = await _searchClient
                .SearchAsync<RetrievedChunkDocument>(queryText, searchOptions, cancellationToken)
                .ConfigureAwait(false);

            var chunks = new List<RetrievedChunk>(capacity: options.TopK);
            await foreach (var result in response.Value.GetResultsAsync().ConfigureAwait(false))
            {
                // Sample the per-result score before the minimum-score
                // filter so dashboards see the full distribution AI Search
                // produced — not just the post-filter shape. This is the
                // signal ADR-0024's cross-encoder gate references.
                EmitScoreSample(result);

                var score = ResolveScore(result);
                if (score < options.MinimumScore)
                {
                    continue;
                }

                chunks.Add(MapToChunk(result.Document, score));
            }

            // ADR-0024 cross-encoder pass: when Rag:CrossEncoder:Enabled=true,
            // re-score the AI Search results with Cohere Rerank-v3 and replace
            // the per-chunk score with the Cohere relevance score. When disabled
            // (default), NullCrossEncoderReranker returns the first TopN unchanged.
            // On a transient Cohere error (429, 503, network) we degrade gracefully
            // to the unranked AI Search results rather than failing the whole query.
            IReadOnlyList<RetrievedChunk> finalChunks = chunks;
            if (_crossEncoderOptions.Enabled)
            {
                try
                {
                    finalChunks = await ApplyRerankingAsync(
                        queryText, chunks, _crossEncoderOptions.TopN, _reranker, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Cohere reranker failed; falling back to unranked AI Search results ({ChunkCount} chunks).",
                        chunks.Count);
                }
            }

            _logger.LogInformation(
                "RAG retrieval: query length={QueryLength}, returned {ChunkCount} chunks above minimum score {MinimumScore} (top {TopK}, reranked={Reranked}, machine={MachineFilter}, document_type={DocumentTypeFilter}, duration={DurationMs:F1}ms).",
                queryText.Length,
                finalChunks.Count,
                options.MinimumScore,
                options.TopK,
                _crossEncoderOptions.Enabled,
                options.MachineId ?? "(any)",
                options.DocumentType ?? "(any)",
                stopwatch.Elapsed.TotalMilliseconds);

            return finalChunks;
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.RagRetrievalDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    // Emit a score sample for a single AI Search result. Tag with
    // `score_source` so dashboards can stratify the distribution by
    // whether the semantic re-ranker engaged or BM25 was the only
    // signal. `fallback_zero` covers the edge case where neither score
    // is present (the SDK guarantees Score, but defensive against trace
    // shape changes in future SDK bumps).
    private static void EmitScoreSample(SearchResult<RetrievedChunkDocument> result)
    {
        var rerankerScore = result.SemanticSearch?.RerankerScore;
        var bm25Score = result.Score;

        if (rerankerScore is double rerank)
        {
            PinballWizardTelemetry.RagRetrievalScoreDistribution.Record(
                rerank,
                new KeyValuePair<string, object?>("score_source", "semantic"));
        }
        else if (bm25Score is double bm25)
        {
            PinballWizardTelemetry.RagRetrievalScoreDistribution.Record(
                bm25,
                new KeyValuePair<string, object?>("score_source", "bm25"));
        }
        else
        {
            PinballWizardTelemetry.RagRetrievalScoreDistribution.Record(
                0.0,
                new KeyValuePair<string, object?>("score_source", "fallback_zero"));
        }
    }

    // Maps RankedChunk results from the cross-encoder back to RetrievedChunk,
    // replacing Score with the Cohere RelevanceScore (cast to double). Static
    // and internal so AiSearchRagRetrieverRerankerTests can exercise it without
    // constructing a full retriever (which needs a live SearchClient).
    internal static async Task<IReadOnlyList<RetrievedChunk>> ApplyRerankingAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        ICrossEncoderReranker reranker,
        CancellationToken cancellationToken)
    {
        var ranked = await reranker
            .RerankAsync(query, candidates, topN, cancellationToken)
            .ConfigureAwait(false);

        var result = new RetrievedChunk[ranked.Count];
        for (var i = 0; i < ranked.Count; i++)
        {
            var r = ranked[i];
            result[i] = r.Chunk with { Score = r.RelevanceScore };
        }
        return result;
    }

    private SearchOptions BuildSearchOptions(
        ReadOnlyMemory<float> queryVector,
        RetrievalOptions options)
        => BuildSearchOptionsCore(queryVector, options, _aiSearchOptions.SemanticConfigName);

    internal static SearchOptions BuildSearchOptionsCore(
        ReadOnlyMemory<float> queryVector,
        RetrievalOptions options,
        string semanticConfigName)
    {
        var searchOptions = new SearchOptions
        {
            // Hybrid retrieval per ADR-0021 § Search defaults: vector
            // (`content_embedding`) + keyword (`content`,
            // `machine_title`, `section_heading`) + semantic ranking.
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryVector)
                    {
                        KNearestNeighborsCount = options.TopK,
                        Fields = { AiSearchIndexFields.ContentEmbedding },
                    },
                },
            },
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = semanticConfigName,
            },
            Size = options.TopK,
            Select =
            {
                AiSearchIndexFields.ChunkId,
                AiSearchIndexFields.MachineId,
                AiSearchIndexFields.MachineTitle,
                AiSearchIndexFields.Manufacturer,
                AiSearchIndexFields.DocumentId,
                AiSearchIndexFields.DocumentUrl,
                AiSearchIndexFields.DocumentType,
                AiSearchIndexFields.PageStart,
                AiSearchIndexFields.PageEnd,
                AiSearchIndexFields.SectionHeading,
                AiSearchIndexFields.Content,
                // Projected for Citation.LastScrapedUtc freshness badge
                // (PR-C3). Never used in ranking or filtering here; the
                // frontend CitationCard reads it from the citation DTO.
                AiSearchIndexFields.LastScrapedUtc,
                // Projected so each chunk self-declares its edition + scope
                // (Task 6/7, AB#259). The Wizard inspects edition_scope to
                // decide R1/R2/R3 and edition to attribute per-edition
                // answers. Retrievable String fields (no IsHidden set in
                // AiSearchIndexSchema) so AI Search returns them.
                AiSearchIndexFields.Edition,
                AiSearchIndexFields.EditionScope,
            },
        };

        var filter = BuildFilter(options);
        if (filter is not null)
        {
            searchOptions.Filter = filter;
        }

        return searchOptions;
    }

    // Compose facet filters into one OData filter expression. All
    // values are escaped per OData rules ('foo''s' for `foo's`) since
    // facet values may originate in untrusted text — a well-formed
    // query string with a `'` in the machine title (e.g. a fan-named
    // machine) shouldn't be allowed to break the filter or inject
    // additional clauses. Returns null when no filters are set so
    // callers can omit `SearchOptions.Filter` rather than passing an
    // always-true predicate.
    internal static string? BuildFilter(RetrievalOptions options)
    {
        var clauses = new List<string>(capacity: 3);

        if (!string.IsNullOrEmpty(options.MachineId))
        {
            clauses.Add(EqualsClause(AiSearchIndexFields.MachineId, options.MachineId));
        }

        if (!string.IsNullOrEmpty(options.DocumentType))
        {
            clauses.Add(EqualsClause(AiSearchIndexFields.DocumentType, options.DocumentType));
        }

        if (!string.IsNullOrEmpty(options.Manufacturer))
        {
            clauses.Add(EqualsClause(AiSearchIndexFields.Manufacturer, options.Manufacturer));
        }

        return clauses.Count == 0 ? null : string.Join(" and ", clauses);
    }

    private static string EqualsClause(string field, string value)
    {
        var sb = new StringBuilder(field.Length + value.Length + 8);
        sb.Append(field).Append(" eq '").Append(EscapeOData(value)).Append('\'');
        return sb.ToString();
    }

    // OData literal escaping: a single quote inside a string literal
    // is doubled. `O'Brien` → `'O''Brien'`. This is the entire escape
    // surface for `eq '...'` filters; field names are caller-trusted
    // constants from `AiSearchIndexFields` so no escaping there.
    private static string EscapeOData(string value)
        => value.Contains('\'', StringComparison.Ordinal)
            ? value.Replace("'", "''", StringComparison.Ordinal)
            : value;

    // Prefer the semantic re-ranker score when present (AI Search emits
    // it whenever `QueryType=Semantic` produced a re-ranked top set);
    // fall back to the BM25 score for results outside the semantic
    // re-ranking window. `Score` is non-nullable on `SearchResult<T>`
    // — the SDK guarantees it on every result row — so the fallback
    // path always has a number, even when semantic ranking is bypassed
    // (e.g. on the first cold-start request before the index has
    // documents).
    private static double ResolveScore(SearchResult<RetrievedChunkDocument> result)
        => ResolveScore(result.SemanticSearch?.RerankerScore, result.Score);

    internal static double ResolveScore(double? rerankerScore, double? bm25Score)
        => rerankerScore ?? bm25Score ?? 0.0;

    internal static RetrievedChunk MapToChunk(RetrievedChunkDocument doc, double score)
        => new(
            ChunkId: doc.ChunkId,
            MachineId: doc.MachineId,
            MachineTitle: doc.MachineTitle,
            Manufacturer: doc.Manufacturer,
            DocumentId: doc.DocumentId,
            DocumentUrl: doc.DocumentUrl,
            DocumentType: doc.DocumentType,
            PageStart: doc.PageStart,
            PageEnd: doc.PageEnd,
            SectionHeading: doc.SectionHeading,
            Content: doc.Content,
            Score: score,
            // LastScrapedUtc projected from the AI Search index's
            // last_scraped_utc field (PR-C3). Null for chunks indexed
            // before PR-C3; propagated to Citation.LastScrapedUtc via
            // SearchCorpusHit → ToolTraceCitationExtractor.
            LastScrapedUtc: doc.LastScrapedUtc,
            // Edition + edition_scope projected from the AI Search index
            // (Task 6/7, AB#259). Threaded through SearchCorpusHit so the
            // Wizard sees each chunk's scope for R1/R2/R3 reasoning. Null
            // for chunks indexed before Task 6 or unresolved documents.
            Edition: doc.Edition,
            EditionScope: doc.EditionScope);
}
