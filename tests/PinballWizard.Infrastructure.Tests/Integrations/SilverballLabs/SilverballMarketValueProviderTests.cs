using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Infrastructure.Integrations.SilverballLabs;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.SilverballLabs;

// Unit tests for SilverballMarketValueProvider.
// ISilverballLabsClient is mocked via NSubstitute so tests exercise the
// provider's mapping and fallback logic without real HTTP.
public sealed class SilverballMarketValueProviderTests
{
    private static readonly SilverballPriceResponseDto FullResponse = new(
        Data: new SilverballPriceDataDto(
            MedianPrice: 7500m,
            AvgPrice: 7800m,
            Min: 5500m,
            Max: 11000m,
            ByCondition:
            [
                new SilverballByConditionDto("mint", 9500m, 3),
                new SilverballByConditionDto("excellent", 8000m, 12),
            ],
            TrendDirection: "stable",
            PriceSummary: "Sells in the $6,000–$10,000 range.",
            LastSaleDate: "2026-06-15"),
        Attribution: new SilverballAttributionDto(
            Source: "Silverball Labs",
            Url: "https://silverballlabs.com/market/GRBNN-MQERZ",
            Text: "Powered by Silverball Labs · Data from PinballPrices.com"));

    private static SilverballMarketValueProvider BuildProvider(ISilverballLabsClient client)
        => new(client, NullLogger<SilverballMarketValueProvider>.Instance);

    // ── Happy path: primary OPDB ID lookup succeeds ─────────────────────────

    [Fact]
    public async Task GetMarketValueAsync_HappyPath_OpdbIdHit_MapsAllFields()
    {
        var client = Substitute.For<ISilverballLabsClient>();
        client.GetByOpdbIdAsync("GRBNN-MQERZ", Arg.Any<CancellationToken>())
              .Returns(FullResponse);

        var result = await BuildProvider(client).GetMarketValueAsync(
            "GRBNN-MQERZ", "Medieval Madness", "Williams", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7500m, result!.MedianPrice);
        Assert.Equal(7800m, result.AvgPrice);
        Assert.Equal(5500m, result.Min);
        Assert.Equal(11000m, result.Max);
        Assert.Equal("stable", result.TrendDirection);
        Assert.Equal("Sells in the $6,000–$10,000 range.", result.PriceSummary);
        Assert.Equal("2026-06-15", result.LastSaleDate);
        Assert.Equal(2, result.ByCondition.Count);
        Assert.Equal("mint", result.ByCondition[0].Condition);
        Assert.Equal(9500m, result.ByCondition[0].MedianPrice);
        Assert.Equal(3, result.ByCondition[0].SaleCount);
        Assert.Equal("Silverball Labs", result.Attribution.Source);
        Assert.Contains("GRBNN-MQERZ", result.Attribution.Url);
        Assert.Contains("PinballPrices.com", result.Attribution.Text);
    }

    // ── Fallback: primary null, name fallback returns data ──────────────────

    [Fact]
    public async Task GetMarketValueAsync_PrimaryNull_NameFallbackReturnsData_ResultNonNull()
    {
        var client = Substitute.For<ISilverballLabsClient>();
        // Primary (OPDB ID) returns null.
        client.GetByOpdbIdAsync("GRBNN-MQERZ", Arg.Any<CancellationToken>())
              .Returns((SilverballPriceResponseDto?)null);
        // Fallback (name) returns data.
        client.GetByNameAsync("Medieval Madness", "Williams", Arg.Any<CancellationToken>())
              .Returns(FullResponse);

        var result = await BuildProvider(client).GetMarketValueAsync(
            "GRBNN-MQERZ", "Medieval Madness", "Williams", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7500m, result!.MedianPrice);
    }

    // ── No opdbId supplied: only name fallback is tried ─────────────────────

    [Fact]
    public async Task GetMarketValueAsync_NoOpdbId_NameFallbackCalled_ReturnsResult()
    {
        var client = Substitute.For<ISilverballLabsClient>();
        client.GetByNameAsync("Addams Family", null, Arg.Any<CancellationToken>())
              .Returns(FullResponse);

        var result = await BuildProvider(client).GetMarketValueAsync(
            null, "Addams Family", null, CancellationToken.None);

        Assert.NotNull(result);
        // Primary was never called — OPDB ID was null so it was skipped.
        await client.DidNotReceive().GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Both lookups null: provider returns null ─────────────────────────────

    [Fact]
    public async Task GetMarketValueAsync_BothNull_ReturnsNull()
    {
        var client = Substitute.For<ISilverballLabsClient>();
        client.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns((SilverballPriceResponseDto?)null);
        client.GetByNameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
              .Returns((SilverballPriceResponseDto?)null);

        var result = await BuildProvider(client).GetMarketValueAsync(
            "GRBNN-MQERZ", "Medieval Madness", "Williams", CancellationToken.None);

        Assert.Null(result);
    }

    // ── No identifiers at all: returns null without calling client ───────────

    [Fact]
    public async Task GetMarketValueAsync_NoIdentifiers_ReturnsNull_NoClientCalls()
    {
        var client = Substitute.For<ISilverballLabsClient>();

        var result = await BuildProvider(client).GetMarketValueAsync(
            null, null, null, CancellationToken.None);

        Assert.Null(result);
        await client.DidNotReceive().GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetByNameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
