using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.GameOverviews;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Rag.GameOverviews;

public sealed class GameOverviewIndexSyncTests
{
    private const string Banner =
        "With your consent, we and other third-party service providers may store cookies on your browser to personalize your experience.";

    [Fact]
    public async Task BannerOnlyOverview_YieldsNoChunks_AndDeletesStaleOverviewChunks()
    {
        var bannerMachine = new Machine
        {
            Id = "G4yZN-MDEP7",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Electronics",
            Title = "Iron Maiden",
            OverviewProse = Banner,
            OverviewSourceUrl = "https://sternpinball.com/game/iron-maiden/",
        };
        var real = new Machine
        {
            Id = "GweeP-MW95j",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla",
            OverviewProse = "Battle Godzilla and rival kaiju across the city in this SPIKE-2 machine.",
            OverviewSourceUrl = "https://sternpinball.com/game/godzilla/",
        };

        var synthesizer = new GameOverviewSynthesizer(NullLogger<GameOverviewSynthesizer>.Instance);
        Assert.Empty(synthesizer.Synthesize(bannerMachine));
        Assert.Contains(synthesizer.Synthesize(real), c => c.Text.Contains("Battle Godzilla", StringComparison.Ordinal));

        var machines = Substitute.For<IMachineRepository>();
        machines.StreamByManufacturerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = call.ArgAt<string>(0);
                return key == ScraperManufacturerKey.Stern
                    ? ToAsync(bannerMachine, real)
                    : ToAsync<Machine>();
            });

        var indexer = Substitute.For<IRagIndexer>();
        indexer.DeleteByDocumentAndMachineAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(2);
        indexer.UpsertAsync(
                Arg.Any<ChunkRequest>(),
                Arg.Any<IReadOnlyList<Chunk>>(),
                Arg.Any<RagIndexerOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new IndexUpsertResult(1, []));

        var sync = new GameOverviewIndexSync(
            machines, synthesizer, indexer, NullLogger<GameOverviewIndexSync>.Instance);

        var result = await sync.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Upserted);
        Assert.Equal(2, result.ChunksDeleted);
        Assert.Equal(0, result.Failed);
        await indexer.Received(1).DeleteByDocumentAndMachineAsync(
            "overview_G4yZN-MDEP7", "G4yZN-MDEP7", Arg.Any<CancellationToken>());
        await indexer.DidNotReceive().DeleteByDocumentAndMachineAsync(
            "overview_GweeP-MW95j", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await indexer.DidNotReceive().UpsertAsync(
            Arg.Is<ChunkRequest>(r => r.MachineId == bannerMachine.Id),
            Arg.Any<IReadOnlyList<Chunk>>(),
            Arg.Any<RagIndexerOptions>(),
            Arg.Any<CancellationToken>());
        await indexer.Received(1).UpsertAsync(
            Arg.Is<ChunkRequest>(r =>
                r.DocumentId == "overview_GweeP-MW95j"
                && r.MachineId == real.Id
                && r.DocumentUrl == real.OverviewSourceUrl
                && r.DocumentType == DocumentType.GameOverview),
            Arg.Is<IReadOnlyList<Chunk>>(chunks => chunks.Count > 0),
            Arg.Any<RagIndexerOptions>(),
            Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
