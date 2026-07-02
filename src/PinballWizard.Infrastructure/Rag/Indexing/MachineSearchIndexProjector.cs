using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Findability;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// ADR-0049 phase 2a: projects all Machine records from Cosmos into the
// AI Search machine findability index. Called from CLI --rebuild-machine-index.
//
// Completeness is computed INLINE here as a simple proportion of non-empty
// data-quality signals. This avoids adding a Core helper for a purely
// infrastructure concern — the computation is purposefully simple and collocated.
// TODO (ADR-0049 phase 2b): reconcile to a shared MachineCompleteness helper if a parallel branch
// introduces one (e.g. for a scoring UI in the admin control plane).
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
                Completeness    = ComputeCompleteness(machine),
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

    // Inline completeness: proportion of key data-quality signals present.
    // Signals checked (7 total):
    //   1. Title is non-empty (always true for any Machine we store — defensive)
    //   2. Year is populated
    //   3. ManufacturerDisplayName is non-empty (always true for stored machines)
    //   4. GroupId is populated (linked to OPDB group)
    //   5. At least one Theme
    //   6. At least one Designer
    //   7. At least one Edition
    //
    // Simple linear score: sum(present) / total_signals.
    // Rationale: canonical OPDB machines have title + year + manufacturer +
    // group + themes; scraper-only machines may lack year/themes/designers.
    // A score of ~0.57 (4/7) is the practical floor for OPDB-linked records.
    // TODO (ADR-0049 phase 2b): reconcile completeness to a shared MachineCompleteness helper.
    internal static double ComputeCompleteness(Core.Domain.Machine machine)
    {
        const int totalSignals = 7;
        var present = 0;

        if (!string.IsNullOrWhiteSpace(machine.Title))                   present++;
        if (machine.Year.HasValue)                                        present++;
        if (!string.IsNullOrWhiteSpace(machine.ManufacturerDisplayName)) present++;
        if (!string.IsNullOrWhiteSpace(machine.GroupId))                 present++;
        if (machine.Themes.Count > 0)                                    present++;
        if (machine.Designers.Count > 0)                                 present++;
        if (machine.Editions.Count > 0)                                  present++;

        return (double)present / totalSignals;
    }
}
