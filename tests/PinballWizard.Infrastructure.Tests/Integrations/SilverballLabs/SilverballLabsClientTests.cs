using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Infrastructure.Integrations.SilverballLabs;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.SilverballLabs;

// Unit tests for SilverballLabsClient.
// Happy path, 404, 429, and 5xx are verified against a stub HttpMessageHandler —
// no live network. The live-contract test against silverballlabs.com is gated
// behind an env var and lives in SilverballLabsClientLiveContractTests.
public sealed class SilverballLabsClientTests : IDisposable
{
    private readonly QueueingHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly SilverballLabsClient _client;

    public SilverballLabsClientTests()
    {
        _http = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://silverballlabs.com/api/v1/"),
        };
        _client = new SilverballLabsClient(_http, NullLogger<SilverballLabsClient>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    // ── GetByOpdbIdAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetByOpdbIdAsync_200WithFullPayload_ParsesAllFields()
    {
        // Arrange — response shape mirrors the live API as documented in ADR-0045.
        _handler.MapJson(
            "https://silverballlabs.com/api/v1/prices/GRBNN-MQERZ",
            FullResponseJson("GRBNN-MQERZ"));

        // Act
        var result = await _client.GetByOpdbIdAsync("GRBNN-MQERZ", CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result!.Data);
        Assert.Equal(7500.00m, result.Data!.MedianPrice);
        Assert.Equal(7800.00m, result.Data.AvgPrice);
        Assert.Equal(5500.00m, result.Data.Min);
        Assert.Equal(11000.00m, result.Data.Max);
        Assert.Equal("stable", result.Data.TrendDirection);
        Assert.NotEmpty(result.Data.PriceSummary!);
        Assert.Equal("2026-06-15", result.Data.LastSaleDate);
        Assert.NotNull(result.Data.ByCondition);
        Assert.Equal(2, result.Data.ByCondition!.Count);
        Assert.Equal("mint", result.Data.ByCondition[0].Condition);
        Assert.Equal(9500.00m, result.Data.ByCondition[0].MedianPrice);
        Assert.Equal(3, result.Data.ByCondition[0].SaleCount);
        Assert.Equal("excellent", result.Data.ByCondition[1].Condition);
        // Attribution
        Assert.NotNull(result.Attribution);
        Assert.Equal("Silverball Labs", result.Attribution!.Source);
        Assert.Contains("silverballlabs.com", result.Attribution.Url!);
    }

    [Fact]
    public async Task GetByOpdbIdAsync_404_ReturnsNull_NoException()
    {
        _handler.Map(
            "https://silverballlabs.com/api/v1/prices/UNKNOWN-ID",
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await _client.GetByOpdbIdAsync("UNKNOWN-ID", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByOpdbIdAsync_429_ReturnsNull_NoException()
    {
        _handler.Map(
            "https://silverballlabs.com/api/v1/prices/RATE-LIMITED",
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await _client.GetByOpdbIdAsync("RATE-LIMITED", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByOpdbIdAsync_500_ReturnsNull_NoException()
    {
        _handler.Map(
            "https://silverballlabs.com/api/v1/prices/SERVER-ERR",
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await _client.GetByOpdbIdAsync("SERVER-ERR", CancellationToken.None);

        Assert.Null(result);
    }

    // ── GetByNameAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetByNameAsync_WithManufacturer_200_ParsesPayload()
    {
        _handler.MapJson(
            "https://silverballlabs.com/api/v1/prices?gameName=Medieval%20Madness&manufacturer=Williams",
            FullResponseJson("name-match"));

        var result = await _client.GetByNameAsync(
            "Medieval Madness", "Williams", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7500.00m, result!.Data!.MedianPrice);
    }

    [Fact]
    public async Task GetByNameAsync_WithoutManufacturer_OmitsManufacturerParam()
    {
        _handler.MapJson(
            "https://silverballlabs.com/api/v1/prices?gameName=Addams%20Family",
            FullResponseJson("name-only"));

        var result = await _client.GetByNameAsync(
            "Addams Family", null, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetByNameAsync_404_ReturnsNull_NoException()
    {
        _handler.Map(
            "https://silverballlabs.com/api/v1/prices?gameName=Nonexistent%20Game",
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await _client.GetByNameAsync(
            "Nonexistent Game", null, CancellationToken.None);

        Assert.Null(result);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    // Produces a canonical full-response JSON matching the ADR-0045 API shape.
    private static string FullResponseJson(string opdbId) => JsonSerializer.Serialize(new
    {
        data = new
        {
            medianPrice = 7500.00,
            avgPrice = 7800.00,
            min = 5500.00,
            max = 11000.00,
            byCondition = new[]
            {
                new { condition = "mint", medianPrice = 9500.00, saleCount = 3 },
                new { condition = "excellent", medianPrice = 8000.00, saleCount = 12 },
            },
            byYear = Array.Empty<object>(),
            trendDirection = "stable",
            priceSummary = "Medieval Madness consistently sells in the $6,000–$10,000 range.",
            lastSaleDate = "2026-06-15",
            // marketInsight deliberately excluded per ADR-0045; not mapped in the DTO
        },
        attribution = new
        {
            source = "Silverball Labs",
            url = $"https://silverballlabs.com/market/{opdbId}",
            text = "Powered by Silverball Labs · Data from PinballPrices.com",
        },
    });
}
