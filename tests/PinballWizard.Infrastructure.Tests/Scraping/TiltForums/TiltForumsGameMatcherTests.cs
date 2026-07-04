using NSubstitute;
using PinballWizard.Core.Domain;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.TiltForums;

public sealed class TiltForumsGameMatcherTests
{
    private static Machine MakeMachine(
        string id, string manufacturerKey, string manufacturerDisplayName, string title,
        string? groupId = null, int? year = null) => new()
    {
        Id = id,
        PartitionKey = manufacturerKey,
        ManufacturerDisplayName = manufacturerDisplayName,
        Title = title,
        GroupId = groupId,
        Year = year,
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
        Assert.Single(result.Machines);
        Assert.Equal("GweeP-MW95j", result.Machines[0].MachineId);
        Assert.Equal("Godzilla", result.Machines[0].MachineTitle);
        Assert.Equal("Stern Pinball", result.Machines[0].ManufacturerDisplayName);
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
        Assert.Empty(result.Machines);
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchesSameGroupAndYear_ReturnsEditionFamily_FansOutToFullSiblingSet()
    {
        // Two Stern Godzilla bases share GroupId "GweeP" and release year 2021 —
        // a genuine edition family. The title-matched candidates are the query
        // result, but the fan-out set must come from GetSiblingsByGroupIdAsync,
        // NOT just the title-matched candidates — proven here by a third sibling
        // ("Godzilla Collector's Edition") that carries different title text and
        // would never have matched the original QueryByTitleAsync("Godzilla") call.
        var pro = MakeMachine("GweeP-MW95j", "stern", "Stern Pinball", "Godzilla", "GweeP", 2021);
        var premium = MakeMachine("GweeP-Ml9pZ", "stern", "Stern Pinball", "Godzilla", "GweeP", 2021);
        var collectors = MakeMachine("GweeP-Xk2Qp", "stern", "Stern Pinball", "Godzilla Collector's Edition", "GweeP", 2021);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([pro, premium]));
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([pro, premium, collectors]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Godzilla", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.ResolvedEditionFamily, result.Status);
        Assert.Equal(3, result.Machines.Count);
        Assert.Contains(result.Machines, m => m.MachineId == "GweeP-MW95j");
        Assert.Contains(result.Machines, m => m.MachineId == "GweeP-Ml9pZ");
        Assert.Contains(result.Machines, m => m.MachineId == "GweeP-Xk2Qp" && m.MachineTitle == "Godzilla Collector's Edition");
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchesDifferentGroups_ReturnsMultipleMatches_NotGuessed()
    {
        // Same title, same manufacturer partition, but genuinely different
        // games (different GroupId/Year) — must stay ambiguous, never fanned out.
        var edition1 = MakeMachine("ABCD-1", "stern", "Stern Pinball", "Some Game", "ABCD", 1994);
        var edition2 = MakeMachine("WXYZ-1", "stern", "Stern Pinball", "Some Game", "WXYZ", 2019);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Some Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([edition1, edition2]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Some Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
        repo.DidNotReceive().GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchesMissingGroupOrYear_ReturnsMultipleMatches_NotGuessed()
    {
        // Two machines sharing a title in the same partition but with no
        // GroupId/Year data at all — cannot be proven an edition family, so
        // this must NOT be guessed as a fan-out either.
        var a = MakeMachine("ABCD-1", "stern", "Stern Pinball", "Some Game");
        var b = MakeMachine("ABCD-2", "stern", "Stern Pinball", "Some Game");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Some Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([a, b]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Some Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
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
        Assert.Equal("JJP-1", result.Machines[0].MachineId);
    }

    [Fact]
    public async Task ResolveAsync_ZeroCandidatesAtAll_ReturnsNoMatch()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Nonexistent Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Nonexistent Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
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
