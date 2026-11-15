using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Players;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.Tools;

// Behavior tests for IfpaWpprTool (ADR-0067).
public sealed class IfpaWpprToolTests
{
    private static IfpaWpprTool NewTool(IIfpaPlayerProvider? provider = null) =>
        new(NullLogger<IfpaWpprTool>.Instance, provider);

    [Fact]
    public async Task Search_WhenProviderAbsent_ReturnsNull()
    {
        var tool = NewTool(provider: null);
        Assert.Null(await tool.SearchIfpaPlayersAsync("Sharpe"));
    }

    [Fact]
    public async Task Profile_WhenProviderAbsent_ReturnsNull()
    {
        var tool = NewTool(provider: null);
        Assert.Null(await tool.GetIfpaPlayerAsync(4));
    }

    [Fact]
    public async Task Search_EmptyName_ReturnsNullWithoutCallingProvider()
    {
        var provider = Substitute.For<IIfpaPlayerProvider>();
        var tool = NewTool(provider);
        Assert.Null(await tool.SearchIfpaPlayersAsync("  "));
        await provider.DidNotReceiveWithAnyArgs()
            .SearchPlayersAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Search_MapsHitsAndAttribution()
    {
        var provider = Substitute.For<IIfpaPlayerProvider>();
        provider.SearchPlayersAsync("Sharpe", "US", "IL", Arg.Any<CancellationToken>())
            .Returns([
                new IfpaPlayerSearchHit(
                    4, "Josh", "Sharpe", "Chicago", "IL", "United States", "US",
                    10, 1234.56, "https://www.ifpapinball.com/player.php?p=4"),
            ]);

        var tool = NewTool(provider);
        var result = await tool.SearchIfpaPlayersAsync("Sharpe", "US", "IL");

        Assert.NotNull(result);
        Assert.Single(result!.Players);
        Assert.Equal(4, result.Players[0].PlayerId);
        Assert.Equal(10, result.Players[0].WpprRank);
        Assert.Contains("player.php?p=4", result.PublicUrl, StringComparison.Ordinal);
        Assert.Contains("IFPA", result.Attribution, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_InvalidId_ReturnsNullWithoutCallingProvider()
    {
        var provider = Substitute.For<IIfpaPlayerProvider>();
        var tool = NewTool(provider);
        Assert.Null(await tool.GetIfpaPlayerAsync(0));
        await provider.DidNotReceiveWithAnyArgs()
            .GetPlayerAsync(default, default);
    }

    [Fact]
    public async Task Profile_MapsMainStandingAndAttribution()
    {
        var provider = Substitute.For<IIfpaPlayerProvider>();
        provider.GetPlayerAsync(4, Arg.Any<CancellationToken>())
            .Returns(new IfpaPlayerProfile(
                4, "Josh", "Sharpe", "JLS", "Chicago", "IL", "United States", "US",
                true,
                "https://www.ifpapinball.com/player.php?p=4",
                new IfpaWpprStanding("MAIN", 10, 11, 1710.11, 1500.0, 15, 2311, 151, 40),
                Women: null));

        var tool = NewTool(provider);
        var dto = await tool.GetIfpaPlayerAsync(4);

        Assert.NotNull(dto);
        Assert.Equal("Josh", dto!.FirstName);
        Assert.NotNull(dto.Main);
        Assert.Equal(10, dto.Main!.CurrentRank);
        Assert.Equal(1710.11, dto.Main.CurrentPoints);
        Assert.Contains("IFPA", dto.Attribution, StringComparison.Ordinal);
        Assert.Contains("/player.php?p=4", dto.PublicUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_ProviderThrows_ReturnsNull()
    {
        var provider = Substitute.For<IIfpaPlayerProvider>();
        provider.GetPlayerAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("boom"));

        var tool = NewTool(provider);
        Assert.Null(await tool.GetIfpaPlayerAsync(1));
    }

    [Fact]
    public async Task Profile_ProviderThrowsArgumentException_ReturnsNull()
    {
        var provider = Substitute.For<IIfpaPlayerProvider>();
        provider.GetPlayerAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArgumentException("unexpected mapping fault"));

        var tool = NewTool(provider);
        Assert.Null(await tool.GetIfpaPlayerAsync(1));
    }

    [Fact]
    public async Task Search_ProviderThrowsInvalidOperation_ReturnsNull()
    {
        var provider = Substitute.For<IIfpaPlayerProvider>();
        provider.SearchPlayersAsync("x", null, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("circuit open"));

        var tool = NewTool(provider);
        Assert.Null(await tool.SearchIfpaPlayersAsync("x"));
    }

    [Fact]
    public async Task Profile_CallerCancellation_Propagates()
    {
        var provider = Substitute.For<IIfpaPlayerProvider>();
        provider.GetPlayerAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var tool = NewTool(provider);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.GetIfpaPlayerAsync(1));
    }
}
