using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Kineticist;
using PinballWizard.Application.Observability;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.Tools;

// Behavior-asserting tests for KineticistGuideLinkTool (ADR-0043 Tier B).
public sealed class KineticistGuideLinkToolTests : IDisposable
{
    private readonly MeterListener _meterListener;
    private readonly ConcurrentBag<(string Instrument, double Value, string? Tag)> _recorded = [];

    public KineticistGuideLinkToolTests()
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

    private static KineticistGuideLinkTool NewTool(IKineticistGuideLinkProvider? provider = null) =>
        new(NullLogger<KineticistGuideLinkTool>.Instance, provider);

    private static KineticistGuideLinkResult SampleResult() =>
        new(
            Slug: "godzilla-2021",
            Name: "Godzilla",
            OpdbId: "G33YF",
            FullTutorialLink: "https://www.kineticist.com/news/godzilla-pinball-tutorial",
            GameRulesLink: "https://pinballrulesheets.com/stern/stern-godzilla-rulesheet.html",
            PinballPrimerUrl: null);

    [Fact]
    public async Task GetKineticistGuideLinksAsync_NoProvider_ReturnsNull()
    {
        var tool = NewTool(provider: null);

        var result = await tool.GetKineticistGuideLinksAsync(opdbId: "G33YF");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetKineticistGuideLinksAsync_NoIdentifiers_ReturnsNull()
    {
        var provider = Substitute.For<IKineticistGuideLinkProvider>();
        var tool = NewTool(provider);

        var result = await tool.GetKineticistGuideLinksAsync(opdbId: null, kineticistSlug: null);

        Assert.Null(result);
        await provider.DidNotReceiveWithAnyArgs()
            .GetGuideLinksAsync(default, default, default);
    }

    [Fact]
    public async Task GetKineticistGuideLinksAsync_ProviderReturnsLinks_MapsToDtoPreferringOpdbId()
    {
        var provider = Substitute.For<IKineticistGuideLinkProvider>();
        provider
            .GetGuideLinksAsync("G33YF", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(SampleResult());

        var tool = NewTool(provider);
        var dto = await tool.GetKineticistGuideLinksAsync(
            opdbId: "G33YF",
            kineticistSlug: "should-not-matter",
            machineTitle: "Stern Godzilla");

        Assert.NotNull(dto);
        Assert.Equal("Stern Godzilla", dto!.MachineTitle);
        Assert.Equal("godzilla-2021", dto.KineticistSlug);
        Assert.Equal("https://www.kineticist.com/news/godzilla-pinball-tutorial", dto.FullTutorialLink);
        Assert.Equal("https://pinballrulesheets.com/stern/stern-godzilla-rulesheet.html", dto.GameRulesLink);
        Assert.Equal(dto.FullTutorialLink, dto.AttributionUrl);
        Assert.Contains("Kineticist", dto.AttributionText, StringComparison.OrdinalIgnoreCase);

        // Tool passes through both identifiers; provider owns OPDB-first preference.
        await provider.Received(1).GetGuideLinksAsync("G33YF", "should-not-matter", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetKineticistGuideLinksAsync_ProviderReturnsNull_ReturnsNull()
    {
        var provider = Substitute.For<IKineticistGuideLinkProvider>();
        provider
            .GetGuideLinksAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((KineticistGuideLinkResult?)null);

        var tool = NewTool(provider);
        Assert.Null(await tool.GetKineticistGuideLinksAsync(opdbId: "G33YF"));
    }

    [Fact]
    public async Task GetKineticistGuideLinksAsync_ProviderThrows_FailsClosedReturnsNull()
    {
        var provider = Substitute.For<IKineticistGuideLinkProvider>();
        provider
            .GetGuideLinksAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("upstream boom"));

        var tool = NewTool(provider);
        var result = await tool.GetKineticistGuideLinksAsync(opdbId: "G33YF");

        Assert.Null(result);
        Assert.Contains(
            _recorded,
            r => r.Instrument == "pinwiz.ai.tool_errors_total"
                 && r.Tag == KineticistGuideLinkTool.ToolTagValue);
    }

    [Fact]
    public async Task GetKineticistGuideLinksAsync_RecordsDuration()
    {
        var provider = Substitute.For<IKineticistGuideLinkProvider>();
        provider
            .GetGuideLinksAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(SampleResult());

        var tool = NewTool(provider);
        await tool.GetKineticistGuideLinksAsync(opdbId: "G33YF");

        Assert.Contains(
            _recorded,
            r => r.Instrument == "pinwiz.ai.tool_duration_ms"
                 && r.Tag == KineticistGuideLinkTool.ToolTagValue);
    }
}
