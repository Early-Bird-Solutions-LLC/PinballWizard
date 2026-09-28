using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Rag.GameOverviews;

// Streams every manufacturer partition, synthesizes GameOverview chunks, and
// upserts them. When a machine has nothing left to index — banner-only prose
// filtered out, or the overview cleared because its slug moved — the existing
// overview_* chunks are deleted. Index GC ignores that prefix (those chunks
// have no scraped_documents row), so this sync is the delete path.
public sealed class GameOverviewIndexSync
{
    private static readonly string[] Manufacturers =
    [
        ScraperManufacturerKey.Stern,
        ScraperManufacturerKey.Jjp,
        ScraperManufacturerKey.AmericanPinball,
        ScraperManufacturerKey.Spooky,
        ScraperManufacturerKey.PinballBrothers,
        ScraperManufacturerKey.BarrelsOfFun,
        ScraperManufacturerKey.ChicagoGaming,
        ScraperManufacturerKey.Multimorphic,
    ];

    private readonly IMachineRepository _machines;
    private readonly IGameOverviewSynthesizer _synthesizer;
    private readonly IRagIndexer _indexer;
    private readonly ILogger<GameOverviewIndexSync> _logger;

    public GameOverviewIndexSync(
        IMachineRepository machines,
        IGameOverviewSynthesizer synthesizer,
        IRagIndexer indexer,
        ILogger<GameOverviewIndexSync> logger)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(synthesizer);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(logger);
        _machines = machines;
        _synthesizer = synthesizer;
        _indexer = indexer;
        _logger = logger;
    }

    public async Task<GameOverviewSyncResult> RunAsync(CancellationToken cancellationToken)
    {
        var upserted = 0;
        var chunksDeleted = 0;
        var skipped = 0;
        var failed = 0;
        var indexerOptions = new RagIndexerOptions();

        foreach (var manufacturer in Manufacturers)
        {
            await foreach (var machine in _machines.StreamByManufacturerAsync(manufacturer, cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunks = _synthesizer.Synthesize(machine);
                var documentId = $"overview_{machine.Id}";

                // No prose left, or no source URL to cite. Either way the
                // previous overview_* document is stale. GC will not remove it.
                if (chunks.Count == 0 || string.IsNullOrWhiteSpace(machine.OverviewSourceUrl))
                {
                    try
                    {
                        var removed = await _indexer
                            .DeleteByDocumentAndMachineAsync(documentId, machine.Id, cancellationToken)
                            .ConfigureAwait(false);
                        chunksDeleted += removed;
                        if (removed > 0)
                        {
                            _logger.LogInformation(
                                "Game overview sync deleted {Deleted} stale chunk(s) for {MachineId} ({DocumentId}).",
                                removed, machine.Id, documentId);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex,
                            "Game overview sync failed to delete stale chunks for {MachineId} ({DocumentId}).",
                            machine.Id, documentId);
                        failed++;
                        continue;
                    }

                    skipped++;
                    continue;
                }

                var chunkRequest = new ChunkRequest(
                    MachineId: machine.Id,
                    MachineTitle: machine.Title,
                    Manufacturer: machine.ManufacturerDisplayName,
                    DocumentId: documentId,
                    DocumentUrl: machine.OverviewSourceUrl,
                    DocumentType: DocumentType.GameOverview,
                    LastScrapedUtc: machine.LastSeenAt == default ? null : machine.LastSeenAt);

                try
                {
                    var result = await _indexer
                        .UpsertAsync(chunkRequest, chunks, indexerOptions, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Failures.Count > 0)
                    {
                        foreach (var failure in result.Failures)
                        {
                            _logger.LogError(
                                "AI Search rejected game-overview chunk {ChunkId} for {Title} ({MachineId}): HTTP {StatusCode} — {Error}",
                                failure.ChunkId, machine.Title, machine.Id, failure.StatusCode, failure.ErrorMessage);
                        }

                        failed++;
                    }
                    else
                    {
                        upserted++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Failed to index game overview for {Title} ({MachineId}).",
                        machine.Title, machine.Id);
                    failed++;
                }
            }
        }

        return new GameOverviewSyncResult(upserted, chunksDeleted, skipped, failed);
    }
}

public sealed record GameOverviewSyncResult(int Upserted, int ChunksDeleted, int Skipped, int Failed);
