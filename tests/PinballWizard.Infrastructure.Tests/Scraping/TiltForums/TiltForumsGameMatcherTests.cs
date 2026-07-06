using NSubstitute;
using PinballWizard.Core.Domain;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Findability;
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

    private static MachineSearchHit Hit(string opdbId, string title, string mfrKey,
        string mfrDisplay, string? groupId, int? year, double score) =>
        new(opdbId, title, mfrDisplay, mfrKey, groupId, year, score);

    private static IMachineSearchIndex FakeIndex(params MachineSearchHit[] hits)
    {
        var idx = Substitute.For<IMachineSearchIndex>();
        idx.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
           .Returns(hits);
        return idx;
    }

    [Fact]
    public async Task ResolveAsync_SingleMatchInManufacturerPartition_ReturnsResolved()
    {
        var stern2021 = MakeMachine("GweeP-MW95j", "stern", "Stern Pinball", "Godzilla");
        var sega1998 = MakeMachine("G4O1L-abc12", "sega", "Sega", "Godzilla");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([stern2021, sega1998]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, null, "Godzilla", "Stern Pinball", CancellationToken.None);

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

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, null, "Star Wars", "Stern Pinball", CancellationToken.None);

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

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, null, "Godzilla", "Stern Pinball", CancellationToken.None);

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

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, null, "Some Game", "Stern Pinball", CancellationToken.None);

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

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, null, "Some Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
        repo.DidNotReceive().GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, null, "Wonka", "Jersey Jack Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.Equal("JJP-1", result.Machines[0].MachineId);
    }

    [Fact]
    public async Task ResolveAsync_ZeroCandidatesAtAll_ReturnsNoMatch()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Nonexistent Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, null, "Nonexistent Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
    }

    [Fact]
    public async Task ResolveAsync_ExactHit_DoesNotConsultIndex()
    {
        var stern = MakeMachine("GK17D-a", "stern", "Stern Pinball", "Jurassic Park");
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Jurassic Park", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([stern]));
        var index = FakeIndex();

        var result = await TiltForumsGameMatcher.ResolveAsync(
            repo, index, "Jurassic Park", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.False(result.ResolvedViaFuzzy);
        await index.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ExactMiss_FuzzyResolvesSingleGroup()
    {
        // "Jurassic Park (Stern)" exact-misses; index top hit is "Jurassic Park"
        // (group GK17D). A lower-scored different-title hit ("Home Edition") is noise
        // and must be ignored — not treated as a collision.
        var jp = MakeMachine("GK17D-a", "stern", "Stern Pinball", "Jurassic Park", "GK17D", 2019);
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Jurassic Park (Stern)", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        repo.GetByOpdbIdAsync("GK17D-a", "stern", Arg.Any<CancellationToken>()).Returns(jp);
        repo.GetSiblingsByGroupIdAsync("GK17D", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([jp]));
        var index = FakeIndex(
            Hit("GK17D-a", "Jurassic Park", "stern", "Stern Pinball", "GK17D", 2019, 103.0),
            Hit("GxvvB-h", "Jurassic Park (Home Edition)", "stern", "Stern Pinball", "GxvvB", 2021, 74.0));

        var result = await TiltForumsGameMatcher.ResolveAsync(
            repo, index, "Jurassic Park (Stern)", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.True(result.ResolvedViaFuzzy);
        Assert.Equal("GK17D-a", result.Machines[0].MachineId);
    }

    [Fact]
    public async Task ResolveAsync_ExactMiss_FuzzyEditionFamilyFansOut()
    {
        var pro = MakeMachine("GK17D-a", "stern", "Stern Pinball", "Jurassic Park", "GK17D", 2019);
        var prem = MakeMachine("GK17D-b", "stern", "Stern Pinball", "Jurassic Park", "GK17D", 2019);
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Jurassic Park (Stern)", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        repo.GetByOpdbIdAsync("GK17D-a", "stern", Arg.Any<CancellationToken>()).Returns(pro);
        repo.GetSiblingsByGroupIdAsync("GK17D", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([pro, prem]));
        var index = FakeIndex(
            Hit("GK17D-a", "Jurassic Park", "stern", "Stern Pinball", "GK17D", 2019, 103.0));

        var result = await TiltForumsGameMatcher.ResolveAsync(
            repo, index, "Jurassic Park (Stern)", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.ResolvedEditionFamily, result.Status);
        Assert.True(result.ResolvedViaFuzzy);
        Assert.Equal(2, result.Machines.Count);
    }

    [Fact]
    public async Task ResolveAsync_ExactMiss_SameTitleDifferentGroup_IsAmbiguous_NotGuessed()
    {
        // Two identically-titled machines in DIFFERENT groups within the scoped
        // partition — a genuine collision. Must NOT be grounded.
        var a = MakeMachine("Gaaa-1", "stern", "Stern Pinball", "Star Trek", "Gaaa", 2013);
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Star Trek", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        repo.GetByOpdbIdAsync("Gaaa-1", "stern", Arg.Any<CancellationToken>()).Returns(a);
        var index = FakeIndex(
            Hit("Gaaa-1", "Star Trek", "stern", "Stern Pinball", "Gaaa", 2013, 90.0),
            Hit("Gbbb-1", "Star Trek", "stern", "Stern Pinball", "Gbbb", 2018, 88.0));

        var result = await TiltForumsGameMatcher.ResolveAsync(
            repo, index, "Star Trek", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
        repo.DidNotReceive().GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ExactMiss_NoFuzzyHits_ReturnsNoMatch()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Weird Al", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        var index = FakeIndex(); // zero hits

        var result = await TiltForumsGameMatcher.ResolveAsync(
            repo, index, "Weird Al", "Multimorphic", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
    }

    [Fact]
    public async Task ResolveAsync_ExactMiss_StaleIndexTopHit_ReturnsNoMatch()
    {
        // Index hit exists but the machine row is gone from Cosmos (stale index).
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Pokemon", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
        repo.GetByOpdbIdAsync("GV8wB-x", "stern", Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        var index = FakeIndex(
            Hit("GV8wB-x", "Pokémon", "stern", "Stern Pinball", "GV8wB", 2026, 17.0));

        var result = await TiltForumsGameMatcher.ResolveAsync(
            repo, index, "Pokemon", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
    }

    [Fact]
    public async Task ResolveAsync_NullIndex_ExactMissStaysNoMatch_NoFuzzy()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Pokemon", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var result = await TiltForumsGameMatcher.ResolveAsync(
            repo, machineSearchIndex: null, "Pokemon", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.False(result.ResolvedViaFuzzy);
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
