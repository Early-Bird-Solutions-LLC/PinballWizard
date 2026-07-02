using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Findability;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// ADR-0049 phase 2a: projects all Machine records from Cosmos into the
// AI Search machine findability index. Called from CLI --rebuild-machine-index.
//
// Completeness uses MachineCompleteness.Score(machine) / 6.0 — the same 6-signal
// Core formula used by the content-intrinsic tie-break in MachineGroundingTool.
// Normalizing to [0,1] keeps the scoring-profile magnitude range valid.
//
// Batching: AI Search upsert accepts up to 1,000 documents per batch. We
// use 100 to stay well under the 16 MB request size limit while keeping
// round-trip count manageable for the ~3,000-machine corpus.
public sealed class MachineSearchIndexProjector(
    SearchClient searchClient,
    IMachineRepository machineRepository,
    IOptions<AiSearchOptions> options,
    ILogger<MachineSearchIndexProjector> logger) : IMachineSearchIndexProjector
{
    private const int BatchSize = 100;

    private readonly SearchClient _searchClient = searchClient;
    private readonly IMachineRepository _machineRepository = machineRepository;
    private readonly AiSearchOptions _options = options.Value;
    private readonly ILogger<MachineSearchIndexProjector> _logger = logger;

    public async Task<MachineIndexProjectionResult> ProjectAllAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var projected = 0;
        var failed = 0;
        var batch = new List<MachineSearchDocument>(BatchSize);

        _logger.LogInformation(
            "Machine index projection started: targetIndex={IndexName}",
            _options.MachineIndexName);

        await foreach (var machine in _machineRepository.StreamAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var doc = new MachineSearchDocument
            {
                Id              = machine.Id,
                Title           = machine.Title,
                TitlePrefix     = machine.Title,   // same source; edge-n-gram analyzer differs
                TitlePhonetic   = machine.Title,   // same source; phonetic analyzer differs
                Manufacturer    = machine.ManufacturerDisplayName,
                ManufacturerKey = machine.PartitionKey,
                Designers       = machine.Designers,
                Themes          = machine.Themes,
                Year            = machine.Year,
                GroupId         = machine.GroupId,
                EditionLabel    = machine.EditionLabel,
                Completeness    = MachineCompleteness.Score(machine) / 6.0,
                LastUpdatedUtc  = machine.LastSeenAt == default
                    ? machine.FirstSeenAt
                    : machine.LastSeenAt,
            };

            batch.Add(doc);

            if (batch.Count >= BatchSize)
            {
                var batchFailed = await FlushBatchAsync(batch, cancellationToken)
                    .ConfigureAwait(false);
                projected += batch.Count - batchFailed;
                failed    += batchFailed;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            var batchFailed = await FlushBatchAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            projected += batch.Count - batchFailed;
            failed    += batchFailed;
        }

        var duration = DateTimeOffset.UtcNow - started;
        _logger.LogInformation(
            "Machine index projection complete: projected={Projected} failed={Failed} durationMs={DurationMs}",
            projected, failed, duration.TotalMilliseconds);

        PinballWizardTelemetry.MachineIndexProjected.Add(projected);
        PinballWizardTelemetry.MachineIndexProjectionDurationMs.Record(duration.TotalMilliseconds);

        return new MachineIndexProjectionResult(projected, failed, duration);
    }

    private async Task<int> FlushBatchAsync(
        List<MachineSearchDocument> batch,
        CancellationToken cancellationToken)
    {
        try
        {
            var actions = batch.Select(IndexDocumentsAction.MergeOrUpload);
            var response = await _searchClient
                .IndexDocumentsAsync(IndexDocumentsBatch.Create(actions.ToArray()), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var batchFailed = 0;
            foreach (var result in response.Value.Results)
            {
                if (!result.Succeeded)
                {
                    _logger.LogWarning(
                        "Machine index: failed to upsert document id={DocId} status={Status} error={Error}",
                        result.Key, result.Status, result.ErrorMessage);
                    batchFailed++;
                }
            }

            return batchFailed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Machine index: batch upsert failed for {Count} documents; skipping batch.",
                batch.Count);
            return batch.Count;
        }
    }

}
