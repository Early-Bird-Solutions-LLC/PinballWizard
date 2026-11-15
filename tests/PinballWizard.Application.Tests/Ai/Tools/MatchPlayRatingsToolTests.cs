using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Tournaments;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.Tools;

// Behavior tests for MatchPlayRatingsTool (ADR-0065).
public sealed class MatchPlayRatingsToolTests
{
    private static MatchPlayRatingsTool NewTool(IMatchPlayRatingsProvider? provider = null) =>
        new(NullLogger<MatchPlayRatingsTool>.Instance, provider);

    private static MatchPlayRatingRow SampleRow(
        string name = "Ada Lovelace",
        int userId = 101,
        int? ifpaId = 42,
        double rating = 1850.5) =>
        new(name, userId, ifpaId, rating, 45.2, 1760.1, "2026-11-14", 3);

    [Fact]
    public async Task Search_WhenProviderAbsent_ReturnsNull()
    {
        Assert.Null(await NewTool(null).SearchMatchPlayRatingsAsync("Ada"));
    }

    [Fact]
    public async Task Compare_WhenProviderAbsent_ReturnsNull()
    {
        Assert.Null(await NewTool(null).CompareMatchPlayRatingsAsync("Ada", "Bob"));
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsNullWithoutCallingProvider()
    {
        var provider = Substitute.For<IMatchPlayRatingsProvider>();
        Assert.Null(await NewTool(provider).SearchMatchPlayRatingsAsync("  "));
        await provider.DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default, default);
    }

    [Fact]
    public async Task Search_MapsHitsAndAttribution()
    {
        var provider = Substitute.For<IMatchPlayRatingsProvider>();
        provider.AttributionUrl.Returns(
            "https://docs.matchplay.events/additional-features/match-play-ratings");
        provider.ExportUrl.Returns(
            "https://mp-ratings.sfo3.cdn.digitaloceanspaces.com/latest-ratings.csv");
        provider.SearchAsync("Ada", 10, Arg.Any<CancellationToken>())
            .Returns([SampleRow()]);

        var dto = await NewTool(provider).SearchMatchPlayRatingsAsync("Ada");

        Assert.NotNull(dto);
        Assert.Equal("Ada", dto!.Query);
        Assert.Single(dto.Hits);
        Assert.Equal("Ada Lovelace", dto.Hits[0].Name);
        Assert.Equal(1850.5, dto.Hits[0].Rating);
        Assert.Contains("Match Play Ratings", dto.Attribution, StringComparison.Ordinal);
        Assert.Contains("match-play-ratings", dto.AttributionUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_NoHits_ReturnsNull()
    {
        var provider = Substitute.For<IMatchPlayRatingsProvider>();
        provider.SearchAsync("zzz", 10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MatchPlayRatingRow>());

        Assert.Null(await NewTool(provider).SearchMatchPlayRatingsAsync("zzz"));
    }

    [Fact]
    public async Task Compare_MapsBothSides()
    {
        var provider = Substitute.For<IMatchPlayRatingsProvider>();
        provider.AttributionUrl.Returns(
            "https://docs.matchplay.events/additional-features/match-play-ratings");
        provider.ExportUrl.Returns("https://example.test/ratings.csv");
        provider.CompareAsync("Ada", "Bob", Arg.Any<CancellationToken>())
            .Returns((SampleRow("Ada"), SampleRow("Bob", 202, 7, 1600)));

        var dto = await NewTool(provider).CompareMatchPlayRatingsAsync("Ada", "Bob");

        Assert.NotNull(dto);
        Assert.Equal("Ada", dto!.Left!.Name);
        Assert.Equal("Bob", dto.Right!.Name);
        Assert.Contains("Match Play Ratings", dto.Attribution, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_BothUnresolved_ReturnsNull()
    {
        var provider = Substitute.For<IMatchPlayRatingsProvider>();
        provider.CompareAsync("x", "y", Arg.Any<CancellationToken>())
            .Returns(((MatchPlayRatingRow?)null, (MatchPlayRatingRow?)null));

        Assert.Null(await NewTool(provider).CompareMatchPlayRatingsAsync("x", "y"));
    }

    [Fact]
    public async Task Compare_OneSidedHit_ReturnsDto()
    {
        var provider = Substitute.For<IMatchPlayRatingsProvider>();
        provider.AttributionUrl.Returns(
            "https://docs.matchplay.events/additional-features/match-play-ratings");
        provider.ExportUrl.Returns("https://example.test/ratings.csv");
        provider.CompareAsync("Ada", "zzz", Arg.Any<CancellationToken>())
            .Returns((SampleRow("Ada"), (MatchPlayRatingRow?)null));

        var dto = await NewTool(provider).CompareMatchPlayRatingsAsync("Ada", "zzz");

        Assert.NotNull(dto);
        Assert.Equal("Ada", dto!.Left!.Name);
        Assert.Null(dto.Right);
    }

    [Fact]
    public async Task Search_ProviderThrows_ReturnsNull()
    {
        var provider = Substitute.For<IMatchPlayRatingsProvider>();
        provider.SearchAsync("Ada", 10, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("cdn down"));

        Assert.Null(await NewTool(provider).SearchMatchPlayRatingsAsync("Ada"));
    }

    [Fact]
    public async Task Compare_ProviderThrows_ReturnsNull()
    {
        var provider = Substitute.For<IMatchPlayRatingsProvider>();
        provider.CompareAsync("Ada", "Bob", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("empty csv"));

        Assert.Null(await NewTool(provider).CompareMatchPlayRatingsAsync("Ada", "Bob"));
    }
}
