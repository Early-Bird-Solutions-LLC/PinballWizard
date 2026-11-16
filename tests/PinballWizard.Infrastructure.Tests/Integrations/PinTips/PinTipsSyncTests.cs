using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Integrations.PinTips;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.PinTips;

public sealed class PinTipsSyncTests
{
    private const string ExportUrl = "https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-pintips.json";
    private static readonly float[] OneQuarter = [0.25f];

    [Fact]
    public async Task SyncAsync_OneTipOnTwoMachines_EmbedsOnceAndUploadsBoth()
    {
        var embedder = Substitute.For<IChunkEmbedder>();
        var embedCalls = 0;
        embedder.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Interlocked.Increment(ref embedCalls);
                var texts = call.Arg<IReadOnlyList<string>>();
                IReadOnlyList<ReadOnlyMemory<float>> vectors = texts
                    .Select(_ => (ReadOnlyMemory<float>)OneQuarter)
                    .ToArray();
                return Task.FromResult(vectors);
            });

        var uploads = new ConcurrentBag<(string MachineId, float First)>();
        var indexer = Substitute.For<IRagIndexer>();
        indexer.UploadEmbeddedAsync(
                Arg.Any<ChunkRequest>(),
                Arg.Any<IReadOnlyList<Chunk>>(),
                Arg.Any<IReadOnlyList<ReadOnlyMemory<float>>>(),
                Arg.Any<RagIndexerOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChunkRequest>();
                var vectors = call.Arg<IReadOnlyList<ReadOnlyMemory<float>>>();
                uploads.Add((request.MachineId, vectors[0].Span[0]));
                return new IndexUpsertResult(1, []);
            });

        var indexState = Substitute.For<IIndexState>();
        indexState.GetLastIndexedHashAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var sync = Build(embedder, indexer, indexState, Substitute.For<IRawDocumentRepository>(), concurrency: 8);
        var tip = new PinTip(2, "G5pe4", "general", 15, "Shoot the castle repeatedly.");

        var result = await sync.SyncAsync(
            [tip],
            ExportUrl,
            (_, _) => Task.FromResult<IReadOnlyList<PinTipsIndexTarget>>(
            [
                new("G5pe4-A", "Game A", "Stern"),
                new("G5pe4-B", "Game B", "Stern"),
            ]),
            CancellationToken.None);

        Assert.Equal(1, embedCalls);
        Assert.Equal(1, result.EmbeddingCalls);
        Assert.Equal(2, result.Indexed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, uploads.Count);
        Assert.Equal(["G5pe4-A", "G5pe4-B"], uploads.Select(u => u.MachineId).Order(StringComparer.Ordinal).ToArray());
        Assert.All(uploads, upload => Assert.Equal(0.25f, upload.First));
        await indexState.Received(2).RecordIndexedAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_UnchangedHashOnEveryMachine_DoesNotEmbed()
    {
        var tip = new PinTip(2, "G5pe4", "general", 15, "Shoot the castle repeatedly.");
        var hash = PinTipsSynthesizer.ContentHash(tip, ExportUrl);
        var embedder = Substitute.For<IChunkEmbedder>();
        var indexer = Substitute.For<IRagIndexer>();
        var indexState = Substitute.For<IIndexState>();
        indexState.GetLastIndexedHashAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(hash);

        var sync = Build(embedder, indexer, indexState, Substitute.For<IRawDocumentRepository>(), concurrency: 4);
        var result = await sync.SyncAsync(
            [tip],
            ExportUrl,
            (_, _) => Task.FromResult<IReadOnlyList<PinTipsIndexTarget>>(
            [
                new("G5pe4-A", "Game A", "Stern"),
                new("G5pe4-B", "Game B", "Stern"),
            ]),
            CancellationToken.None);

        Assert.Equal(2, result.SkippedUnchanged);
        Assert.Equal(0, result.EmbeddingCalls);
        Assert.Equal(0, result.Indexed);
        await embedder.DidNotReceiveWithAnyArgs().EmbedBatchAsync(default!, default);
        await indexer.DidNotReceiveWithAnyArgs().UploadEmbeddedAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task SyncAsync_CitationWriteFails_DoesNotStampTheSkipHash()
    {
        var embedder = Substitute.For<IChunkEmbedder>();
        embedder.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IReadOnlyList<string>>();
                IReadOnlyList<ReadOnlyMemory<float>> vectors = texts
                    .Select(_ => (ReadOnlyMemory<float>)OneQuarter)
                    .ToArray();
                return Task.FromResult(vectors);
            });
        var indexer = Substitute.For<IRagIndexer>();
        indexer.UploadEmbeddedAsync(
                Arg.Any<ChunkRequest>(),
                Arg.Any<IReadOnlyList<Chunk>>(),
                Arg.Any<IReadOnlyList<ReadOnlyMemory<float>>>(),
                Arg.Any<RagIndexerOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new IndexUpsertResult(1, []));
        var indexState = Substitute.For<IIndexState>();
        indexState.GetLastIndexedHashAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var raw = Substitute.For<IRawDocumentRepository>();
        raw.UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("cosmos down"));

        var sync = Build(embedder, indexer, indexState, raw, concurrency: 2);
        var result = await sync.SyncAsync(
            [new PinTip(2, "G5pe4", "general", 15, "Shoot the castle repeatedly.")],
            ExportUrl,
            (_, _) => Task.FromResult<IReadOnlyList<PinTipsIndexTarget>>([new("G5pe4-A", "Game A", "Stern")]),
            CancellationToken.None);

        Assert.Equal(1, result.Indexed);
        Assert.Equal(1, result.RawDocWriteFailed);
        await indexState.DidNotReceiveWithAnyArgs().RecordIndexedAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task SyncAsync_ConcurrencyCap_DoesNotEmbedMoreTipsThanTheCap()
    {
        var embedder = new HoldingEmbedder();
        var indexer = Substitute.For<IRagIndexer>();
        indexer.UploadEmbeddedAsync(
                Arg.Any<ChunkRequest>(),
                Arg.Any<IReadOnlyList<Chunk>>(),
                Arg.Any<IReadOnlyList<ReadOnlyMemory<float>>>(),
                Arg.Any<RagIndexerOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new IndexUpsertResult(1, []));
        var indexState = Substitute.For<IIndexState>();
        indexState.GetLastIndexedHashAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var sync = Build(embedder, indexer, indexState, Substitute.For<IRawDocumentRepository>(), concurrency: 2);
        var tips = Enumerable.Range(1, 4)
            .Select(id => new PinTip(id, $"G{id}", "general", 1, $"Tip body {id} is long enough to chunk."))
            .ToArray();

        var result = await sync.SyncAsync(
            tips,
            ExportUrl,
            (groupId, _) => Task.FromResult<IReadOnlyList<PinTipsIndexTarget>>(
                [new($"{groupId}-A", groupId, "Stern")]),
            CancellationToken.None);

        Assert.Equal(4, result.Indexed);
        Assert.Equal(4, result.EmbeddingCalls);
        Assert.Equal(2, embedder.MaxInFlight);
    }

    private static PinTipsSync Build(
        IChunkEmbedder embedder,
        IRagIndexer indexer,
        IIndexState indexState,
        IRawDocumentRepository rawDocuments,
        int concurrency)
    {
        var synthesizer = new PinTipsSynthesizer(
            new HybridChunker(Options.Create(new ChunkerOptions()), NullLogger<HybridChunker>.Instance),
            NullLogger<PinTipsSynthesizer>.Instance);
        return new PinTipsSync(
            synthesizer,
            embedder,
            indexer,
            indexState,
            rawDocuments,
            Options.Create(new PinTipsOptions { ExportUrl = ExportUrl, SyncConcurrency = concurrency }),
            NullLogger<PinTipsSync>.Instance);
    }

    private sealed class HoldingEmbedder : IChunkEmbedder
    {
        private static readonly float[] Half = [0.5f];
        private int _inFlight;
        private int _max;

        public int MaxInFlight => _max;

        public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int snapshot;
            do
            {
                snapshot = _max;
                if (now <= snapshot)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _max, now, snapshot) != snapshot);

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            Interlocked.Decrement(ref _inFlight);
            return texts.Select(_ => (ReadOnlyMemory<float>)Half).ToArray();
        }
    }
}
