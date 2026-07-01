using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Indexing;

namespace PinballWizard.Application.Rag.Ingestion;

// Default `IRagIndexGarbageCollector`. Pure Application-layer
// orchestration over three abstractions:
//   - IIndexedPairSource       — distinct (document, machine) pairs in the index
//   - IScrapedDocumentRepository — the authoritative fan-out rows in Cosmos
//   - IRagIndexer              — the delete capability (per (document, machine))
//
// Algorithm: enumerate the index's distinct pairs, group them by
// document, and for each document ask the catalog which machines it is
// legitimately fanned out to. Any index pair whose machine is NOT in that
// backing set is an orphan (its fan-out row was pruned but the change
// feed couldn't signal the delete) and its chunks are removed.
//
// One StreamByDocumentIdAsync call per distinct document (not per pair)
// keeps the Cosmos read count proportional to document count, not chunk
// count. Idempotent: a second run finds no orphans and deletes nothing.
public sealed class RagIndexGarbageCollector : IRagIndexGarbageCollector
{
    private readonly IIndexedPairSource _pairSource;
    private readonly IScrapedDocumentRepository _documentRepository;
    private readonly IRagIndexer _indexer;
    private readonly ILogger<RagIndexGarbageCollector> _logger;

    public RagIndexGarbageCollector(
        IIndexedPairSource pairSource,
        IScrapedDocumentRepository documentRepository,
        IRagIndexer indexer,
        ILogger<RagIndexGarbageCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(pairSource);
        ArgumentNullException.ThrowIfNull(documentRepository);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(logger);
        _pairSource = pairSource;
        _documentRepository = documentRepository;
        _indexer = indexer;
        _logger = logger;
    }

    // The RAG index holds several document classes, but only scraped-document
    // manuals/bulletins (deterministic id "doc_…", per the provenance model in
    // CLAUDE.md) are backed by scraped_documents and reconcilable against it.
    // Synthesized classes — metadata cards ("meta_…"), game overviews
    // ("overview_…"), Kineticist tutorials, TWIP news ("twip_…") — are populated
    // directly from the machines container / external sources and have NO
    // scraped_documents row by design. The GC MUST ignore them, or it would
    // delete every synthesized chunk (they'd all look like orphans).
    private const string ScrapedDocumentIdPrefix = "doc_";

    public async Task<RagIndexGcResult> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        // Collect the index's distinct SCRAPED-document pairs, grouped by
        // document, so we issue one catalog lookup per document rather than per
        // pair. Non-scraped classes are skipped up front — they are out of this
        // GC's authority (their source of truth is not scraped_documents).
        var machinesByDocument = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pairsScanned = 0;
        var skippedNonScraped = 0;
        await foreach (var pair in _pairSource.StreamIndexedPairsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!pair.DocumentId.StartsWith(ScrapedDocumentIdPrefix, StringComparison.Ordinal))
            {
                skippedNonScraped++;
                continue;
            }

            pairsScanned++;
            if (!machinesByDocument.TryGetValue(pair.DocumentId, out var machines))
            {
                machines = [];
                machinesByDocument[pair.DocumentId] = machines;
            }
            machines.Add(pair.MachineId);
        }

        _logger.LogInformation(
            "RAG index GC starting ({Mode}): {PairCount} scraped-document (document, machine) pairs across " +
            "{DocumentCount} documents; skipped {SkippedNonScraped} synthesized chunks " +
            "(metadata cards / overviews / news — not reconciled against scraped_documents).",
            dryRun ? "dry-run" : "delete",
            pairsScanned,
            machinesByDocument.Count,
            skippedNonScraped);

        var orphanPairs = 0;
        var chunksDeleted = 0;

        foreach (var (documentId, indexedMachines) in machinesByDocument)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The catalog's backing machines for this document. A document
            // with NO fan-out rows (fully deleted) yields an empty set — so
            // every indexed machine for it is an orphan.
            var backingMachines = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var machineId in _documentRepository
                .StreamByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false))
            {
                backingMachines.Add(machineId);
            }

            foreach (var indexedMachine in indexedMachines)
            {
                if (backingMachines.Contains(indexedMachine))
                {
                    continue;
                }

                orphanPairs++;
                if (dryRun)
                {
                    _logger.LogInformation(
                        "RAG index GC (dry-run): orphan pair document={DocumentId} machine={MachineId} " +
                        "has no scraped_documents row — would delete its chunks.",
                        documentId, indexedMachine);
                    continue;
                }

                var deleted = await _indexer
                    .DeleteByDocumentAndMachineAsync(documentId, indexedMachine, cancellationToken)
                    .ConfigureAwait(false);
                chunksDeleted += deleted;
                _logger.LogInformation(
                    "RAG index GC: deleted {Deleted} orphan chunks for document={DocumentId} machine={MachineId} " +
                    "(no backing scraped_documents row).",
                    deleted, documentId, indexedMachine);
            }
        }

        _logger.LogInformation(
            "RAG index GC complete ({Mode}): scanned={PairsScanned} orphanPairs={OrphanPairs} chunksDeleted={ChunksDeleted}.",
            dryRun ? "dry-run" : "delete",
            pairsScanned,
            orphanPairs,
            chunksDeleted);

        return new RagIndexGcResult(pairsScanned, orphanPairs, chunksDeleted, dryRun);
    }
}
