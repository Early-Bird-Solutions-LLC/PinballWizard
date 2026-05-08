using Azure;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// Idempotent ensure-created step for the AI Search RAG index. Safe to
// call on every startup, and from the CLI's `--ensure-rag-index`
// post-deploy smoke-test. Mirrors the shape of `CosmosBootstrapper`
// (Cosmos DB + container ensure) and `AzureAiSearchSmokeProbe` (basic
// service reachability) — this class adds the index-existence check
// that the smoke probe deferred to W2-3.
//
// First-run: creates `pinwiz-rag-v1` from `AiSearchIndexSchema.Build`.
// Subsequent runs: when the index exists already, the operation is a
// no-op (existence check returns the index; we don't redeploy schema
// — `CreateOrUpdateIndex` could mutate vector profile / semantic
// config in subtle ways, so we deliberately skip it).
//
// A schema-breaking change ships via the v1→v2 cutover documented in
// ADR-0021 § Versioning strategy: bump the index name in
// `AiSearchOptions.IndexName`, redeploy, run the bootstrapper to
// create v2, re-ingest, swap the retriever's reads. This bootstrapper
// stays index-name-agnostic — it ensures whatever the configured
// name is.
public sealed class RagIndexBootstrapper
{
    private readonly SearchIndexClient _indexClient;
    private readonly AiSearchOptions _options;
    private readonly ILogger<RagIndexBootstrapper> _logger;

    public RagIndexBootstrapper(
        SearchIndexClient indexClient,
        IOptions<AiSearchOptions> options,
        ILogger<RagIndexBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(indexClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _indexClient = indexClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RagIndexBootstrapResult> EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.IndexName))
        {
            throw new InvalidOperationException(
                $"AiSearch:IndexName is empty; cannot ensure RAG index without a target name.");
        }

        try
        {
            var existing = await _indexClient
                .GetIndexAsync(_options.IndexName, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "RAG index already present: name={IndexName} fields={FieldCount}",
                existing.Value.Name,
                existing.Value.Fields.Count);
            return new RagIndexBootstrapResult(_options.IndexName, Created: false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation(
                "RAG index missing; creating from schema. name={IndexName} semantic={SemanticConfigName}",
                _options.IndexName,
                _options.SemanticConfigName);

            var index = AiSearchIndexSchema.Build(_options.IndexName, _options.SemanticConfigName);
            await _indexClient
                .CreateIndexAsync(index, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "RAG index created: name={IndexName} fields={FieldCount}",
                index.Name,
                index.Fields.Count);
            return new RagIndexBootstrapResult(_options.IndexName, Created: true);
        }
    }
}

public sealed record RagIndexBootstrapResult(string IndexName, bool Created);
