using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// Idempotent ensure-created step for the AI Search machine index and its
// synonym map (ADR-0049 phase 2a). Mirrors the shape of RagIndexBootstrapper
// (which does the same for the corpus index). Safe to call from CLI
// --rebuild-machine-index on every run.
//
// Schema rebuild behavior:
//   EnsureCreatedAsync  — creates only when missing; no-op if present
//   RecreateAsync       — drops + recreates (DESTRUCTIVE; empties the index;
//                         requires re-projection via MachineSearchIndexProjector)
//
// Synonym map behavior: CreateOrUpdateSynonymMapAsync is always called
// (idempotent: creates on first call, updates on subsequent calls).
// This means synonym additions in the seed file deploy on the next
// --rebuild-machine-index run without any schema version bump.
public sealed class MachineSearchIndexBootstrapper(
    SearchIndexClient indexClient,
    IOptions<AiSearchOptions> options,
    ILogger<MachineSearchIndexBootstrapper> logger)
{
    private readonly SearchIndexClient _indexClient = indexClient;
    private readonly AiSearchOptions _options = options.Value;
    private readonly ILogger<MachineSearchIndexBootstrapper> _logger = logger;

    public async Task<MachineIndexBootstrapResult> EnsureCreatedAsync(
        string synonymsText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.MachineIndexName))
        {
            throw new InvalidOperationException(
                "AiSearch:MachineIndexName is empty; cannot ensure machine index without a target name.");
        }

        // Always update the synonym map — synonym additions in the seed file
        // should deploy without a schema version bump.
        await EnsureSynonymMapAsync(synonymsText, cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = await _indexClient
                .GetIndexAsync(_options.MachineIndexName, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Machine index already present: name={IndexName} fields={FieldCount}",
                existing.Value.Name,
                existing.Value.Fields.Count);

            return new MachineIndexBootstrapResult(_options.MachineIndexName, Created: false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation(
                "Machine index missing; creating from schema. name={IndexName}",
                _options.MachineIndexName);

            var index = MachineSearchIndexSchema.Build(_options.MachineIndexName);
            await _indexClient
                .CreateIndexAsync(index, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Machine index created: name={IndexName} fields={FieldCount}",
                index.Name,
                index.Fields.Count);

            return new MachineIndexBootstrapResult(_options.MachineIndexName, Created: true);
        }
    }

    // DESTRUCTIVE: drops the machine index and recreates it empty.
    // The index is a rebuildable projection of Cosmos, so corrections
    // (e.g. field renames, analyzer changes) are applied by wipe-and-rebuild,
    // never by in-place schema mutation. Re-project with --rebuild-machine-index
    // (which calls EnsureCreatedAsync then MachineSearchIndexProjector.ProjectAllAsync).
    public async Task<MachineIndexBootstrapResult> RecreateAsync(
        string synonymsText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.MachineIndexName))
        {
            throw new InvalidOperationException(
                "AiSearch:MachineIndexName is empty; cannot rebuild machine index without a target name.");
        }

        try
        {
            await _indexClient
                .DeleteIndexAsync(_options.MachineIndexName, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "Machine index DROPPED for rebuild: name={IndexName}. Re-project with --rebuild-machine-index.",
                _options.MachineIndexName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation(
                "Machine index already absent before rebuild: name={IndexName} (treating as dropped).",
                _options.MachineIndexName);
        }

        return await EnsureCreatedAsync(synonymsText, cancellationToken).ConfigureAwait(false);
    }

    // Creates or updates the synonym map. Idempotent: AI Search's
    // CreateOrUpdateSynonymMapAsync is a safe upsert. The synonym map
    // resource is separate from the index; once created it can be updated
    // without rebuilding the index (the attachment is by name).
    private async Task EnsureSynonymMapAsync(string synonymsText, CancellationToken cancellationToken)
    {
        var synonymMap = new SynonymMap(MachineSearchIndexSchema.SynonymMapName, synonymsText);

        await _indexClient
            .CreateOrUpdateSynonymMapAsync(synonymMap, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Machine synonym map ensured: name={SynonymMapName}",
            MachineSearchIndexSchema.SynonymMapName);
    }
}

public sealed record MachineIndexBootstrapResult(string IndexName, bool Created);
