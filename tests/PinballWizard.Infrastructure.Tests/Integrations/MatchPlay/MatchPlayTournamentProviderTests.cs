using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.MatchPlay;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.MatchPlay;

public sealed class MatchPlayTournamentProviderTests
{
    [Fact]
    public async Task GetStandingsAsync_JoinsPlayerNames_AndCachesCompleted()
    {
        var client = Substitute.For<IMatchPlayClient>();
        client.GetTournamentAsync(7, MatchPlayTournamentInclude.Players, Arg.Any<CancellationToken>())
            .Returns(new MatchPlayTournamentWireDto
            {
                TournamentId = 7,
                Name = "Open",
                Status = "completed",
                Players =
                [
                    new MatchPlayPlayerWireDto { PlayerId = 1, Name = "Ada", IfpaId = 5 },
                ],
            });
        client.GetStandingsAsync(7, Arg.Any<CancellationToken>())
            .Returns([
                new MatchPlayStandingWireDto
                {
                    PlayerId = 1, Position = 1, Points = "8.00", GamesPlayed = 4,
                },
            ]);

        var provider = new MatchPlayTournamentProvider(
            client,
            Options.Create(new MatchPlayOptions { ApiToken = "t", StandingsMinIntervalSeconds = 15 }),
            NullLogger<MatchPlayTournamentProvider>.Instance);

        var first = await provider.GetStandingsAsync(7);
        var second = await provider.GetStandingsAsync(7);

        Assert.NotNull(first);
        Assert.Equal("Ada", first!.Standings[0].PlayerName);
        Assert.Equal(first, second);
        await client.Received(1).GetStandingsAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetArenasAsync_MapsOpdbIds()
    {
        var client = Substitute.For<IMatchPlayClient>();
        client.GetTournamentAsync(7, MatchPlayTournamentInclude.Arenas, Arg.Any<CancellationToken>())
            .Returns(new MatchPlayTournamentWireDto
            {
                TournamentId = 7,
                Name = "Open",
                Status = "started",
                Arenas =
                [
                    new MatchPlayArenaWireDto
                    {
                        ArenaId = 2, Name = "Godzilla", Status = "active", OpdbId = "G33YF",
                    },
                ],
            });

        var provider = new MatchPlayTournamentProvider(
            client,
            Options.Create(new MatchPlayOptions { ApiToken = "t" }),
            NullLogger<MatchPlayTournamentProvider>.Instance);

        var arenas = await provider.GetArenasAsync(7);

        Assert.NotNull(arenas);
        Assert.Equal("G33YF", arenas!.Arenas[0].OpdbId);
    }

    [Fact]
    public async Task FindPlayersAsync_MatchesNameAndIfpa()
    {
        var client = Substitute.For<IMatchPlayClient>();
        client.GetTournamentAsync(7, MatchPlayTournamentInclude.Players, Arg.Any<CancellationToken>())
            .Returns(new MatchPlayTournamentWireDto
            {
                TournamentId = 7,
                Name = "Open",
                Status = "started",
                Players =
                [
                    new MatchPlayPlayerWireDto { PlayerId = 1, Name = "Ada Lovelace", IfpaId = 42 },
                    new MatchPlayPlayerWireDto { PlayerId = 2, Name = "Bob", IfpaId = 7 },
                ],
            });

        var provider = new MatchPlayTournamentProvider(
            client,
            Options.Create(new MatchPlayOptions { ApiToken = "t" }),
            NullLogger<MatchPlayTournamentProvider>.Instance);

        var byName = await provider.FindPlayersAsync(7, "ada");
        var byIfpa = await provider.FindPlayersAsync(7, "42");

        Assert.Single(byName!.Players);
        Assert.Equal(1, byName.Players[0].PlayerId);
        Assert.Single(byIfpa!.Players);
        Assert.Equal("Ada Lovelace", byIfpa.Players[0].Name);
    }

    [Fact]
    public async Task GetRoundsAndGamesAsync_JoinsArenaAndPlayers()
    {
        var client = Substitute.For<IMatchPlayClient>();
        client.GetTournamentAsync(
                7,
                MatchPlayTournamentInclude.Players | MatchPlayTournamentInclude.Arenas,
                Arg.Any<CancellationToken>())
            .Returns(new MatchPlayTournamentWireDto
            {
                TournamentId = 7,
                Name = "Open",
                Status = "started",
                Players =
                [
                    new MatchPlayPlayerWireDto { PlayerId = 1, Name = "Ada" },
                    new MatchPlayPlayerWireDto { PlayerId = 2, Name = "Bob" },
                ],
                Arenas =
                [
                    new MatchPlayArenaWireDto { ArenaId = 9, Name = "MM", OpdbId = "Gx" },
                ],
            });
        client.GetRoundsAsync(7, Arg.Any<CancellationToken>())
            .Returns([
                new MatchPlayRoundWireDto
                {
                    RoundId = 3, TournamentId = 7, Index = 0, Name = "Round 1", Status = "started",
                },
            ]);
        client.GetTournamentGamesAsync(
                7,
                status: "started",
                playerId: null,
                arenaId: null,
                roundId: null,
                Arg.Any<CancellationToken>())
            .Returns([
                new MatchPlayGameWireDto
                {
                    GameId = 11,
                    RoundId = 3,
                    TournamentId = 7,
                    ArenaId = 9,
                    Status = "started",
                    PlayerIds = [1, 2],
                },
            ]);

        var provider = new MatchPlayTournamentProvider(
            client,
            Options.Create(new MatchPlayOptions { ApiToken = "t" }),
            NullLogger<MatchPlayTournamentProvider>.Instance);

        var floor = await provider.GetRoundsAndGamesAsync(7);

        Assert.NotNull(floor);
        Assert.Single(floor!.Rounds);
        Assert.Equal("MM", floor.Games[0].ArenaName);
        Assert.Equal("Ada", floor.Games[0].Players[0].PlayerName);
    }

    [Fact]
    public async Task SearchTournamentsAsync_MapsPublicUrl()
    {
        var client = Substitute.For<IMatchPlayClient>();
        client.SearchTournamentsAsync("x", Arg.Any<CancellationToken>())
            .Returns([
                new MatchPlayTournamentWireDto
                {
                    TournamentId = 3, Name = "X", Status = "planned",
                },
            ]);

        var provider = new MatchPlayTournamentProvider(
            client,
            Options.Create(new MatchPlayOptions { ApiToken = "t" }),
            NullLogger<MatchPlayTournamentProvider>.Instance);

        var hits = await provider.SearchTournamentsAsync("x");

        Assert.Single(hits);
        Assert.Equal("https://app.matchplay.events/tournaments/3", hits[0].PublicUrl);
    }
}
