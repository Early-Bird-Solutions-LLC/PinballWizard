using NSubstitute;
using PinballWizard.Infrastructure.Integrations.Ifpa;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Ifpa;

public sealed class IfpaPlayerProviderTests
{
    [Fact]
    public async Task SearchPlayersAsync_MapsWireHitsToPublicUrl()
    {
        var client = Substitute.For<IIfpaClient>();
        client.SearchPlayersAsync("Sharpe", null, null, Arg.Any<CancellationToken>())
            .Returns([
                new IfpaPlayerSearchHitWireDto
                {
                    PlayerId = 4,
                    FirstName = "Josh",
                    LastName = "Sharpe",
                    WpprRank = 10,
                    City = "Chicago",
                    StateProv = "IL",
                    CountryCode = "US",
                    CountryName = "United States",
                },
            ]);

        var provider = new IfpaPlayerProvider(client);
        var hits = await provider.SearchPlayersAsync("Sharpe");

        Assert.Single(hits);
        Assert.Equal(4, hits[0].PlayerId);
        Assert.Equal("https://www.ifpapinball.com/player.php?p=4", hits[0].PublicUrl);
        Assert.Equal(10, hits[0].WpprRank);
    }

    [Fact]
    public async Task GetPlayerAsync_MapsOpenAndWomensStandings_AsMainAndWomen()
    {
        var client = Substitute.For<IIfpaClient>();
        client.GetPlayerAsync(4, Arg.Any<CancellationToken>())
            .Returns(new IfpaPlayerWireDto
            {
                PlayerId = 4,
                FirstName = "Josh",
                LastName = "Sharpe",
                IfpaRegistered = true,
                PlayerStats = new IfpaPlayerStatsWireDto
                {
                    System = new IfpaRankingSystemsWireDto
                    {
                        Open = new IfpaRankingStandingWireDto
                        {
                            CurrentRank = 46,
                            CurrentPoints = 1544.39,
                        },
                        Womens = new IfpaRankingStandingWireDto
                        {
                            CurrentRank = 1,
                            CurrentPoints = 900,
                        },
                    },
                },
            });

        var provider = new IfpaPlayerProvider(client);
        var profile = await provider.GetPlayerAsync(4);

        Assert.NotNull(profile);
        Assert.Equal("MAIN", profile!.Main!.RankingSystem);
        Assert.Equal(46, profile.Main.CurrentRank);
        Assert.Equal("WOMEN", profile.Women!.RankingSystem);
        Assert.Equal(1, profile.Women.CurrentRank);
        Assert.Equal("https://www.ifpapinball.com/player.php?p=4", profile.PublicUrl);
    }

    [Fact]
    public async Task GetPlayerAsync_FallsBackToHistoricalMainWomenKeys()
    {
        var client = Substitute.For<IIfpaClient>();
        client.GetPlayerAsync(4, Arg.Any<CancellationToken>())
            .Returns(new IfpaPlayerWireDto
            {
                PlayerId = 4,
                FirstName = "Josh",
                LastName = "Sharpe",
                PlayerStats = new IfpaPlayerStatsWireDto
                {
                    System = new IfpaRankingSystemsWireDto
                    {
                        Main = new IfpaRankingStandingWireDto
                        {
                            CurrentRank = 10,
                            CurrentPoints = 1710.11,
                        },
                        Women = new IfpaRankingStandingWireDto
                        {
                            CurrentRank = 1,
                            CurrentPoints = 900,
                        },
                    },
                },
            });

        var provider = new IfpaPlayerProvider(client);
        var profile = await provider.GetPlayerAsync(4);

        Assert.Equal(10, profile!.Main!.CurrentRank);
        Assert.Equal(1, profile.Women!.CurrentRank);
    }

    [Fact]
    public async Task GetPlayerAsync_NullWire_ReturnsNull()
    {
        var client = Substitute.For<IIfpaClient>();
        client.GetPlayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((IfpaPlayerWireDto?)null);

        var provider = new IfpaPlayerProvider(client);
        Assert.Null(await provider.GetPlayerAsync(99));
    }
}
