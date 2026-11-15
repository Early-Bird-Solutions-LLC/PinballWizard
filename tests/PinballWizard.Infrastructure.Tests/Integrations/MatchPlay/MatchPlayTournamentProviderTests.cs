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
        client.GetTournamentAsync(7, true, Arg.Any<CancellationToken>())
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
