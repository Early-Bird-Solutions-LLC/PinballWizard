using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Integrations.PinTips;

public sealed record PinTipsSyncResult(
    int Indexed,
    int SkippedUnchanged,
    int Unlinked,
    int Failed,
    int RawDocWriteFailed,
    int EmbeddingCalls);

// Indexes the PinTips export. One tip's text is embedded once and then
// uploaded under every catalog machine it fans out to. Several tips run
// at once, capped by PinTipsOptions.SyncConcurrency. A failure on one
// machine does not stop the rest of the export.
public sealed class PinTipsSync
{
    private readonly PinTipsSynthesizer _synthesizer;
    private readonly IChunkEmbedder _embedder;
    private readonly IRagIndexer _indexer;
    private readonly IIndexState _indexState;
    private readonly IRawDocumentRepository _rawDocuments;
    private readonly PinTipsOptions _options;
    private readonly ILogger<PinTipsSync> _logger;

    public PinTipsSync(
        PinTipsSynthesizer synthesizer,
        IChunkEmbedder embedder,
        IRagIndexer indexer,
        IIndexState indexState,
        IRawDocumentRepository rawDocuments,
        IOptions<PinTipsOptions> options,
        ILogger<PinTipsSync> logger)
    {
        ArgumentNullException.ThrowIfNull(synthesizer);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(indexState);
        ArgumentNullException.ThrowIfNull(rawDocuments);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _synthesizer = synthesizer;
        _embedder = embedder;
        _indexer = indexer;
        _indexState = indexState;
        _rawDocuments = rawDocuments;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PinTipsSyncResult> SyncAsync(
        IReadOnlyList<PinTip> tips,
        string exportUrl,
        Func<string, CancellationToken, Task<IReadOnlyList<PinTipsIndexTarget>>> loadGroup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tips);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportUrl);
        ArgumentNullException.ThrowIfNull(loadGroup);

        var concurrency = _options.SyncConcurrency;
        if (concurrency is < 1 or > 16)
        {
            throw new InvalidOperationException(
                $"PinTips:SyncConcurrency must be in 1..16; got {concurrency}.");
        }

        var counts = new Counts();
        var groups = new Dictionary<string, IReadOnlyList<PinTipsIndexTarget>>(StringComparer.Ordinal);
        var groupGate = new object();
        using var gate = new SemaphoreSlim(concurrency);
        var indexerOptions = new RagIndexerOptions();

        var work = tips.Select(async tip =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await IndexTipAsync(
                    tip, exportUrl, loadGroup, groups, groupGate, counts, indexerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        return new PinTipsSyncResult(
            counts.Indexed,
            counts.SkippedUnchanged,
            counts.Unlinked,
            counts.Failed,
            counts.RawDocWriteFailed,
            counts.EmbeddingCalls);
    }

    private async Task IndexTipAsync(
        PinTip tip,
        string exportUrl,
        Func<string, CancellationToken, Task<IReadOnlyList<PinTipsIndexTarget>>> loadGroup,
        Dictionary<string, IReadOnlyList<PinTipsIndexTarget>> groups,
        object groupGate,
        Counts counts,
        RagIndexerOptions indexerOptions,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PinTipsIndexTarget> groupMachines;
        try
        {
            groupMachines = await MachinesInGroupAsync(tip.OpdbId, loadGroup, groups, groupGate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref counts.Failed);
            _logger.LogWarning(ex, "PinTips tip {TipId} could not load catalog group {OpdbId}.", tip.TipId, tip.OpdbId);
            return;
        }

        var targets = PinTipsCatalogTargets.Resolve(tip.OpdbId, groupMachines);
        if (targets.Count == 0)
        {
            Interlocked.Increment(ref counts.Unlinked);
            return;
        }

        var contentHash = PinTipsSynthesizer.ContentHash(tip, exportUrl);
        var pending = new List<PinTipsIndexTarget>(targets.Count);
        foreach (var target in targets)
        {
            var documentId = DocumentId(tip, target);
            try
            {
                var lastIndexed = await _indexState.GetLastIndexedHashAsync(
                    documentId, target.MachineId, cancellationToken).ConfigureAwait(false);
                if (PinTipsSynthesizer.IsUnchanged(lastIndexed, contentHash))
                {
                    Interlocked.Increment(ref counts.SkippedUnchanged);
                    continue;
                }

                pending.Add(target);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref counts.Failed);
                _logger.LogWarning(
                    ex,
                    "PinTips tip {TipId} hash read failed for {MachineId}.",
                    tip.TipId,
                    target.MachineId);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var sample = pending[0];
        var sampleRequest = ChunkRequestFor(tip, sample, exportUrl);
        var chunks = _synthesizer.Synthesize(tip, exportUrl, sampleRequest);
        if (chunks.Count == 0)
        {
            return;
        }

        IReadOnlyList<ReadOnlyMemory<float>> embeddings;
        try
        {
            var texts = new string[chunks.Count];
            for (var i = 0; i < chunks.Count; i++)
            {
                texts[i] = chunks[i].Text;
            }

            embeddings = await _embedder.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
            if (embeddings.Count != chunks.Count)
            {
                throw new InvalidOperationException(
                    $"Embedder returned {embeddings.Count} vector(s) for {chunks.Count} PinTips chunk(s).");
            }

            Interlocked.Increment(ref counts.EmbeddingCalls);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Add(ref counts.Failed, pending.Count);
            _logger.LogWarning(ex, "PinTips tip {TipId} embedding failed for {Count} machine(s).", tip.TipId, pending.Count);
            return;
        }

        foreach (var target in pending)
        {
            await UploadOneAsync(
                tip, target, exportUrl, contentHash, chunks, embeddings, indexerOptions, counts, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task UploadOneAsync(
        PinTip tip,
        PinTipsIndexTarget target,
        string exportUrl,
        string contentHash,
        IReadOnlyList<Chunk> chunks,
        IReadOnlyList<ReadOnlyMemory<float>> embeddings,
        RagIndexerOptions indexerOptions,
        Counts counts,
        CancellationToken cancellationToken)
    {
        var documentId = DocumentId(tip, target);
        try
        {
            var request = ChunkRequestFor(tip, target, exportUrl);
            var result = await _indexer.UploadEmbeddedAsync(
                request, chunks, embeddings, indexerOptions, cancellationToken).ConfigureAwait(false);
            if (result.Failures.Count > 0)
            {
                Interlocked.Increment(ref counts.Failed);
                _logger.LogWarning(
                    "PinTips tip {TipId} upload failed for {MachineId}: {Error}",
                    tip.TipId,
                    target.MachineId,
                    result.Failures[0].ErrorMessage);
                return;
            }

            Interlocked.Increment(ref counts.Indexed);
            if (!await TryPersistCitationAsync(tip, target, documentId, exportUrl, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref counts.RawDocWriteFailed);
                return;
            }

            try
            {
                await _indexState.RecordIndexedAsync(
                    documentId, target.MachineId, contentHash, chunks.Count, 0, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "PinTips index-state stamp failed for {DocumentId}. The next sync will re-embed it.",
                    documentId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref counts.Failed);
            _logger.LogWarning(ex, "PinTips tip {TipId} failed for {MachineId}.", tip.TipId, target.MachineId);
        }
    }

    private async Task<bool> TryPersistCitationAsync(
        PinTip tip,
        PinTipsIndexTarget target,
        string documentId,
        string exportUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = SynthesizedSourceDescriptors.PinTips;
            var record = SynthesizedDocumentRecordFactory.Create(
                documentId,
                PinTipsSynthesizer.TitleFor(tip),
                exportUrl,
                source.DiscoveryContext,
                source.DocumentType,
                source.FileFormat,
                target.Manufacturer,
                target.MachineTitle,
                null,
                DateTimeOffset.UtcNow);
            await _rawDocuments.UpsertRawAsync(record, cancellationToken).ConfigureAwait(false);
            await _rawDocuments.UpdateLinkStatusAsync(
                documentId,
                LinkStatus.PlatformGeneric,
                "synthesized",
                null,
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PinTips citation row write failed for {DocumentId}.", documentId);
            return false;
        }
    }

    private static async Task<IReadOnlyList<PinTipsIndexTarget>> MachinesInGroupAsync(
        string opdbId,
        Func<string, CancellationToken, Task<IReadOnlyList<PinTipsIndexTarget>>> loadGroup,
        Dictionary<string, IReadOnlyList<PinTipsIndexTarget>> groups,
        object groupGate,
        CancellationToken cancellationToken)
    {
        var dash = opdbId.IndexOf('-', StringComparison.Ordinal);
        var groupId = dash < 0 ? opdbId : opdbId[..dash];

        lock (groupGate)
        {
            if (groups.TryGetValue(groupId, out var cached))
            {
                return cached;
            }
        }

        var loaded = await loadGroup(groupId, cancellationToken).ConfigureAwait(false);

        lock (groupGate)
        {
            if (!groups.TryGetValue(groupId, out var stored))
            {
                stored = loaded;
                groups[groupId] = stored;
            }

            return stored;
        }
    }

    private static ChunkRequest ChunkRequestFor(PinTip tip, PinTipsIndexTarget target, string exportUrl) =>
        new(
            MachineId: target.MachineId,
            MachineTitle: target.MachineTitle,
            Manufacturer: target.Manufacturer,
            DocumentId: DocumentId(tip, target),
            DocumentUrl: exportUrl,
            DocumentType: DocumentType.PlayTip,
            LastScrapedUtc: DateTimeOffset.UtcNow);

    private static string DocumentId(PinTip tip, PinTipsIndexTarget target) =>
        $"pintips_{tip.TipId}_{target.MachineId}";

    private sealed class Counts
    {
        public int Indexed;
        public int SkippedUnchanged;
        public int Unlinked;
        public int Failed;
        public int RawDocWriteFailed;
        public int EmbeddingCalls;
    }
}
