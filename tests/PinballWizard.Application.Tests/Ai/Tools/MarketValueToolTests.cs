using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Pricing;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.Tools;

// Behavior-asserting tests for MarketValueTool (ADR-0045).
// IMarketValueProvider is mocked via NSubstitute to keep tests pure;
// end-to-end integration with the Silverball Labs API is NOT covered here
// (that is SilverballLabsClientTests's domain).
//
// telemetry assertions follow the ConcurrentBag+Assert.Contains pattern
// (MeterListener test pattern, memory feedback_meterlistener_test_pattern).
public sealed class MarketValueToolTests : IDisposable
{
    private readonly MeterListener _meterListener;
    private readonly ConcurrentBag<(string Instrument, double Value, string? Tag)> _recorded = [];

    public MarketValueToolTests()
    {
        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PinballWizardTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _meterListener.SetMeasurementEventCallback<long>((instr, val, tags, _) =>
        {
            var tool = tags.ToArray().FirstOrDefault(t => t.Key == "tool").Value?.ToString();
            _recorded.Add((instr.Name, (double)val, tool));
        });
        _meterListener.SetMeasurementEventCallback<double>((instr, val, tags, _) =>
        {
            var tool = tags.ToArray().FirstOrDefault(t => t.Key == "tool").Value?.ToString();
            _recorded.Add((instr.Name, val, tool));
        });
        _meterListener.Start();
    }

    public void Dispose() => _meterListener.Dispose();

    private static MarketValueTool NewTool(IMarketValueProvider? provider = null) =>
        new(NullLogger<MarketValueTool>.Instance, provider);

    private static MarketValueResult SampleResult(string? opdbId = null) =>
        new(
            MedianPrice: 5500m,
            AvgPrice: 5600m,
            Min: 4500m,
            Max: 7000m,
            ByCondition: [
                new MarketValueByCondition("mint", 6800m, 5),
                new MarketValueByCondition("excellent", 5500m, 15),
                new MarketValueByCondition("good", 4800m, 10),
            ],
            TrendDirection: "stable",
            PriceSummary: "Medieval Madness has held steady around $5,500 for Excellent examples.",
            LastSaleDate: "2026-06-01",
            Attribution: new MarketValueAttribution(
                Source: "Silverball Labs / PinballPrices.com",
                Url: "https://silverballlabs.com/market/MM5K-MRKPL",
                Text: "Powered by Silverball Labs (data sourced from PinballPrices.com)"));

    // ── 1. Provider absent — degrade gracefully ──────────────────────────

    [Fact]
    public async Task GetMarketValueAsync_NoProvider_ReturnsNull()
    {
        // When Silverball Labs is not configured, the tool degrades to null
        // so the Wizard can give an honest "unavailable" answer rather than
        // throwing or fabricating.
        var tool = NewTool(provider: null);

        var result = await tool.GetMarketValueAsync("Medieval Madness");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMarketValueAsync_NoProvider_DoesNotThrow()
    {
        var tool = NewTool(provider: null);

        // Should not throw even with all optional params absent.
        await tool.GetMarketValueAsync("Medieval Madness", opdbId: null, manufacturer: null);
    }

    // ── 2. Provider present, data found ─────────────────────────────────

    [Fact]
    public async Task GetMarketValueAsync_ProviderReturnsResult_MapsToDto()
    {
        var provider = Substitute.For<IMarketValueProvider>();
        provider
            .GetMarketValueAsync("MM5K-MRKPL", "Medieval Madness", "Williams",
                                 Arg.Any<CancellationToken>())
            .Returns(SampleResult("MM5K-MRKPL"));

        var tool = NewTool(provider);

        var dto = await tool.GetMarketValueAsync(
            "Medieval Madness", opdbId: "MM5K-MRKPL", manufacturer: "Williams");

        Assert.NotNull(dto);
        Assert.Equal("Medieval Madness", dto.MachineTitle);
        Assert.Equal(5500m, dto.MedianPrice);
        Assert.Equal(5600m, dto.AvgPrice);
        Assert.Equal(4500m, dto.Min);
        Assert.Equal(7000m, dto.Max);
        Assert.Equal("stable", dto.TrendDirection);
        Assert.Equal("Medieval Madness has held steady around $5,500 for Excellent examples.",
            dto.PriceSummary);
        Assert.Equal("2026-06-01", dto.LastSaleDate);
        Assert.Equal("https://silverballlabs.com/market/MM5K-MRKPL", dto.AttributionUrl);
        Assert.Equal("Powered by Silverball Labs (data sourced from PinballPrices.com)",
            dto.AttributionText);
    }

    [Fact]
    public async Task GetMarketValueAsync_ProviderReturnsResult_MapsConditionsCorrectly()
    {
        var provider = Substitute.For<IMarketValueProvider>();
        provider
            .GetMarketValueAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                                 Arg.Any<CancellationToken>())
            .Returns(SampleResult());

        var tool = NewTool(provider);
        var dto = await tool.GetMarketValueAsync("Medieval Madness");

        Assert.NotNull(dto);
        Assert.Equal(3, dto.ByCondition.Count);
        var mint = dto.ByCondition.Single(c => c.Condition == "mint");
        Assert.Equal(6800m, mint.MedianPrice);
        Assert.Equal(5, mint.SaleCount);
    }

    // ── 3. Provider present, no data ────────────────────────────────────

    [Fact]
    public async Task GetMarketValueAsync_ProviderReturnsNull_ReturnsNull()
    {
        // Provider found no data (e.g. 404 or machine not in pricing database).
        // Tool degrades to null so the Wizard degrades gracefully.
        var provider = Substitute.For<IMarketValueProvider>();
        provider
            .GetMarketValueAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                                 Arg.Any<CancellationToken>())
            .Returns((MarketValueResult?)null);

        var tool = NewTool(provider);
        var result = await tool.GetMarketValueAsync("Unknown Machine");

        Assert.Null(result);
    }

    // ── 4. Telemetry ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetMarketValueAsync_ProviderReturnsResult_RecordsDurationTelemetry()
    {
        // Successful call: a duration measurement must be recorded with
        // tool tag = "getMarketValue". No error counter increment.
        var provider = Substitute.For<IMarketValueProvider>();
        provider
            .GetMarketValueAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                                 Arg.Any<CancellationToken>())
            .Returns(SampleResult());

        var tool = NewTool(provider);
        await tool.GetMarketValueAsync("Medieval Madness");

        _meterListener.RecordObservableInstruments();
        Assert.Contains(_recorded, r =>
            r.Instrument == "pinwiz.ai.tool_duration_ms" &&
            r.Tag == MarketValueTool.ToolTagValue &&
            r.Value >= 0);
        Assert.DoesNotContain(_recorded, r =>
            r.Instrument == "pinwiz.ai.tool_errors_total" &&
            r.Tag == MarketValueTool.ToolTagValue);
    }

    [Fact]
    public async Task GetMarketValueAsync_ProviderThrows_RecordsErrorTelemetry_ReturnsNull()
    {
        // Exception from the provider: fail closed (return null), meter the error,
        // record duration — do NOT re-throw.
        var provider = Substitute.For<IMarketValueProvider>();
        provider
            .GetMarketValueAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                                 Arg.Any<CancellationToken>())
            .Returns(Task.FromException<MarketValueResult?>(new HttpRequestException("Silverball Labs unreachable")));

        var tool = NewTool(provider);

        var result = await tool.GetMarketValueAsync("Medieval Madness");

        Assert.Null(result);
        _meterListener.RecordObservableInstruments();
        Assert.Contains(_recorded, r =>
            r.Instrument == "pinwiz.ai.tool_errors_total" &&
            r.Tag == MarketValueTool.ToolTagValue);
        Assert.Contains(_recorded, r =>
            r.Instrument == "pinwiz.ai.tool_duration_ms" &&
            r.Tag == MarketValueTool.ToolTagValue);
    }
}
