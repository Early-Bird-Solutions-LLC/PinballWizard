using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Rag.Ingestion;

// Behavior tests for the RAG index orphan garbage collector. The GC is
// the delete-propagation mechanism: the Cosmos change feed can't signal
// deletes, so when the linker prunes a fan-out row the index chunks
// linger until this pass reconciles them. Uses hand fakes (not
// NSubstitute) so the async-enumerable pair source and per-document
// valid-machine lookups read cleanly.
public sealed class RagIndexGarbageCollectorTests
{
    [Fact]
    public async Task RunAsync_OrphanPair_DeletesChunksAndCounts()
    {
        // doc_1 is fanned out to two machines in the index, but the
        // catalog only backs mch_right — mch_wrong is an orphan left by
        // a prune/re-attribution and its chunks must be deleted.
        var pairs = new IIndexedPairSourceFake(
            new IndexedPair("doc_1", "mch_right"),
            new IndexedPair("doc_1", "mch_wrong"));
        var repo = new ScrapedDocumentRepositoryFake();
        repo.Add("doc_1", "mch_right"); // only the correct attribution is backed
        var indexer = new RecordingRagIndexer { DeleteResult = 4 };

        var gc = new RagIndexGarbageCollector(pairs, repo, indexer, NullLogger<RagIndexGarbageCollector>.Instance);

        var result = await gc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(2, result.PairsScanned);
        Assert.Equal(1, result.OrphanPairs);
        Assert.Equal(4, result.ChunksDeleted);
        Assert.False(result.DryRun);
        var deleted = Assert.Single(indexer.Deletes);
        Assert.Equal(("doc_1", "mch_wrong"), deleted);
    }

    [Fact]
    public async Task RunAsync_AllPairsBacked_DeletesNothing()
    {
        var pairs = new IIndexedPairSourceFake(
            new IndexedPair("doc_1", "mch_a"),
            new IndexedPair("doc_2", "mch_b"));
        var repo = new ScrapedDocumentRepositoryFake();
        repo.Add("doc_1", "mch_a");
        repo.Add("doc_2", "mch_b");
        var indexer = new RecordingRagIndexer { DeleteResult = 9 };

        var gc = new RagIndexGarbageCollector(pairs, repo, indexer, NullLogger<RagIndexGarbageCollector>.Instance);

        var result = await gc.RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(2, result.PairsScanned);
        Assert.Equal(0, result.OrphanPairs);
        Assert.Equal(0, result.ChunksDeleted);
        Assert.Empty(indexer.Deletes);
    }

    [Fact]
    public async Task RunAsync_DryRun_CountsOrphansButDoesNotDelete()
    {
        var pairs = new IIndexedPairSourceFake(
            new IndexedPair("doc_1", "mch_wrong"));
        var repo = new ScrapedDocumentRepositoryFake(); // doc_1 has NO backing rows
        var indexer = new RecordingRagIndexer { DeleteResult = 4 };

        var gc = new RagIndexGarbageCollector(pairs, repo, indexer, NullLogger<RagIndexGarbageCollector>.Instance);

        var result = await gc.RunAsync(dryRun: true, CancellationToken.None);

        Assert.Equal(1, result.PairsScanned);
        Assert.Equal(1, result.OrphanPairs);
        Assert.Equal(0, result.ChunksDeleted); // nothing deleted in dry run
        Assert.True(result.DryRun);
        Assert.Empty(indexer.Deletes);
    }

    [Fact]
    public async Task RunAsync_IgnoresNonScrapedDocumentClasses()
    {
        // The RAG index holds multiple document classes: scraped-document
        // manuals/bulletins (id "doc_…", backed by scraped_documents) AND
        // synthesized chunks — metadata cards ("meta_…"), game overviews
        // ("overview_…"), news ("twip_…") — which are populated directly from
        // the machines container / external sources and have NO scraped_documents
        // row by design. The GC reconciles ONLY scraped-document pairs; it must
        // NOT treat a synthesized chunk as an orphan (doing so would delete every
        // metadata card / overview / news chunk from the index).
        var pairs = new IIndexedPairSourceFake(
            new IndexedPair("doc_stern_manual", "mch_wrong"),   // real orphan — delete
            new IndexedPair("meta_GweeP-Ml9pZ", "GweeP-Ml9pZ"), // synthesized — ignore
            new IndexedPair("overview_G43BW", "G43BW"),          // synthesized — ignore
            new IndexedPair("twip_some-article", "pinball_news"));// synthesized — ignore
        var repo = new ScrapedDocumentRepositoryFake(); // nothing backed
        var indexer = new RecordingRagIndexer { DeleteResult = 3 };

        var gc = new RagIndexGarbageCollector(pairs, repo, indexer, NullLogger<RagIndexGarbageCollector>.Instance);

        var result = await gc.RunAsync(dryRun: false, CancellationToken.None);

        // Only the doc_ pair is scanned/reconciled and deleted.
        Assert.Equal(1, result.PairsScanned);
        Assert.Equal(1, result.OrphanPairs);
        Assert.Equal(3, result.ChunksDeleted);
        var deleted = Assert.Single(indexer.Deletes);
        Assert.Equal(("doc_stern_manual", "mch_wrong"), deleted);
        // The synthesized classes were never touched.
        Assert.DoesNotContain(indexer.Deletes, d => d.DocumentId.StartsWith("meta_", StringComparison.Ordinal));
        Assert.DoesNotContain(indexer.Deletes, d => d.DocumentId.StartsWith("overview_", StringComparison.Ordinal));
        Assert.DoesNotContain(indexer.Deletes, d => d.DocumentId.StartsWith("twip_", StringComparison.Ordinal));
    }

    // ── fakes ────────────────────────────────────────────────────────

    private sealed class IIndexedPairSourceFake(params IndexedPair[] pairs) : IIndexedPairSource
    {
        public async IAsyncEnumerable<IndexedPair> StreamIndexedPairsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var p in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return p;
                await Task.Yield();
            }
        }
    }

    private sealed class ScrapedDocumentRepositoryFake : IScrapedDocumentRepository
    {
        private readonly Dictionary<string, List<string>> _machinesByDoc = new(StringComparer.Ordinal);

        public void Add(string documentId, string machineId)
        {
            if (!_machinesByDoc.TryGetValue(documentId, out var list))
            {
                list = [];
                _machinesByDoc[documentId] = list;
            }
            list.Add(machineId);
        }

        public async IAsyncEnumerable<string> StreamByDocumentIdAsync(
            string documentId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_machinesByDoc.TryGetValue(documentId, out var list))
            {
                foreach (var m in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return m;
                    await Task.Yield();
                }
            }
        }

        public Task UpsertAsync(DocumentRecord record, string machineId, string machineTitle, string manufacturer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UpsertFromRawAsync(RawDocumentRecord raw, string machineId, string machineTitle, string manufacturer, string? edition, EditionScope editionScope, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteFanOutRowAsync(string documentId, string machineId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingRagIndexer : IRagIndexer
    {
        public List<(string DocumentId, string MachineId)> Deletes { get; } = [];
        public int DeleteResult { get; set; }

        public Task<IndexUpsertResult> UpsertAsync(ChunkRequest request, IReadOnlyList<Chunk> chunks, RagIndexerOptions options, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> DeleteByDocumentAndMachineAsync(string documentId, string machineId, CancellationToken cancellationToken)
        {
            Deletes.Add((documentId, machineId));
            return Task.FromResult(DeleteResult);
        }
    }
}
