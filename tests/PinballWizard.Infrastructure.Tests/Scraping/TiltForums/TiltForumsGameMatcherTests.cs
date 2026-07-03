using NSubstitute;
using PinballWizard.Core.Domain;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.TiltForums;

public sealed class TiltForumsGameMatcherTests
{
    private static Machine MakeMachine(string id, string manufacturerKey, string manufacturerDisplayName, string title) => new()
    {
        Id = id,
        PartitionKey = manufacturerKey,
        ManufacturerDisplayName = manufacturerDisplayName,
        Title = title,
    };

    [Fact]
    public async Task ResolveAsync_SingleMatchInManufacturerPartition_ReturnsResolved()
    {
        var stern2021 = MakeMachine("GweeP-MW95j", "stern", "Stern Pinball", "Godzilla");
        var sega1998 = MakeMachine("G4O1L-abc12", "sega", "Sega", "Godzilla");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([stern2021, sega1998]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Godzilla", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.Equal("GweeP-MW95j", result.MachineId);
        Assert.Equal("Godzilla", result.MachineTitle);
        Assert.Equal("Stern Pinball", result.ManufacturerDisplayName);
    }

    [Fact]
    public async Task ResolveAsync_NoMatchInManufacturerPartition_ReturnsNoMatch()
    {
        // "Star Wars" exists for Bally/Williams only — Stern has no machine
        // by this exact title, so this must NOT fall back to an unscoped
        // guess; it must report NoMatch.
        var williamsStarWars = MakeMachine("G4O1L-MDW47", "williams", "Williams", "Star Wars");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Star Wars", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([williamsStarWars]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Star Wars", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.Null(result.MachineId);
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchesInSamePartition_ReturnsMultipleMatches_NotGuessed()
    {
        var edition1 = MakeMachine("ABCD-1", "stern", "Stern Pinball", "Some Game");
        var edition2 = MakeMachine("ABCD-2", "stern", "Stern Pinball", "Some Game");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Some Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([edition1, edition2]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Some Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
        Assert.Null(result.MachineId);
    }

    [Fact]
    public async Task ResolveAsync_ManufacturerHeaderTextNormalized_MatchesPartitionKey()
    {
        // "Jersey Jack Pinball" (master-list header text) must normalize to
        // partition key "jjp" via the existing OpdbMachineMapper function.
        var jjpMachine = MakeMachine("JJP-1", "jjp", "Jersey Jack Pinball", "Wonka");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Wonka", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([jjpMachine]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Wonka", "Jersey Jack Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.Equal("JJP-1", result.MachineId);
    }

    [Fact]
    public async Task ResolveAsync_ZeroCandidatesAtAll_ReturnsNoMatch()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Nonexistent Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Nonexistent Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
    }

    private static async IAsyncEnumerable<Machine> ToAsyncEnumerable(IEnumerable<Machine> machines)
    {
        foreach (var machine in machines)
        {
            yield return machine;
        }
        await Task.CompletedTask;
    }
}
