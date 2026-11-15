using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Tournaments;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.Tools;

// Behavior tests for MatchPlayTournamentTool (ADR-0061).
public sealed class MatchPlayTournamentToolTests
{
    private static MatchPlayTournamentTool NewTool(IMatchPlayTournamentProvider? provider = null) =>
        new(NullLogger<MatchPlayTournamentTool>.Instance, provider);

    [Fact]
    public async Task Search_WhenProviderAbsent_ReturnsNull()
    {
        var tool = NewTool(provider: null);
        Assert.Null(await tool.SearchMatchPlayTournamentsAsync("INDISC"));
    }

    [Fact]
    public async Task Standings_WhenProviderAbsent_ReturnsNull()
    {
        var tool = NewTool(provider: null);
        Assert.Null(await tool.GetMatchPlayTournamentStandingsAsync(42));
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsNullWithoutCallingProvider()
    {
        var provider = Substitute.For<IMatchPlayTournamentProvider>();
        var tool = NewTool(provider);
        Assert.Null(await tool.SearchMatchPlayTournamentsAsync("  "));
        await provider.DidNotReceiveWithAnyArgs()
            .SearchTournamentsAsync(default!, default);
    }

    [Fact]
    public async Task Search_MapsHits()
    {
        var provider = Substitute.For<IMatchPlayTournamentProvider>();
        provider.SearchTournamentsAsync("INDISC", Arg.Any<CancellationToken>())
            .Returns([
                new MatchPlayTournamentSummary(
                    99, "INDISC 2026", "started",
                    new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
                    "group_matchplay",
                    "https://app.matchplay.events/tournaments/99"),
            ]);

        var tool = NewTool(provider);
        var hits = await tool.SearchMatchPlayTournamentsAsync("INDISC");

        Assert.NotNull(hits);
        Assert.Single(hits!);
        Assert.Equal(99, hits[0].TournamentId);
        Assert.Equal("started", hits[0].Status);
        Assert.Contains("99", hits[0].PublicUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standings_InvalidId_ReturnsNullWithoutCallingProvider()
    {
        var provider = Substitute.For<IMatchPlayTournamentProvider>();
        var tool = NewTool(provider);
        Assert.Null(await tool.GetMatchPlayTournamentStandingsAsync(0));
        await provider.DidNotReceiveWithAnyArgs()
            .GetStandingsAsync(default, default);
    }

    [Fact]
    public async Task Standings_MapsRowsAndAttribution()
    {
        var provider = Substitute.For<IMatchPlayTournamentProvider>();
        provider.GetStandingsAsync(7, Arg.Any<CancellationToken>())
            .Returns(new MatchPlayTournamentStandings(
                7, "Test Open", "completed",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                "https://app.matchplay.events/tournaments/7",
                [
                    new MatchPlayStandingRow(1, 10, "Ada", "12.00", 5, 123),
                    new MatchPlayStandingRow(2, 11, "Bob", "9.00", 5, null),
                ],
                DateTimeOffset.UtcNow));

        var tool = NewTool(provider);
        var dto = await tool.GetMatchPlayTournamentStandingsAsync(7);

        Assert.NotNull(dto);
        Assert.Equal(2, dto!.Standings.Count);
        Assert.Equal("Ada", dto.Standings[0].PlayerName);
        Assert.Contains("Match Play Events", dto.Attribution, StringComparison.Ordinal);
        Assert.Contains("/tournaments/7", dto.PublicUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standings_ProviderThrows_ReturnsNull()
    {
        var provider = Substitute.For<IMatchPlayTournamentProvider>();
        provider.GetStandingsAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("boom"));

        var tool = NewTool(provider);
        Assert.Null(await tool.GetMatchPlayTournamentStandingsAsync(1));
    }
}
