using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for AdminMonitoring.razor (/admin/monitoring).
//
// Static SSR + [StreamRendering] (ADR-0034). All data is hardcoded showcase
// placeholders — no DI, no parameters. Tests assert the four panel sections
// render with the correct structure, key metric values, and state labels.
public sealed class AdminMonitoringTests : AsyncBunitContext
{
    public AdminMonitoringTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
    }

    [Fact]
    public void Renders_WithoutException()
    {
        _ = Render<AdminMonitoring>();
    }

    [Fact]
    public void Toolbar_IsPresent()
    {
        var cut = Render<AdminMonitoring>();
        cut.Find("[data-testid='monitoring-toolbar']");
    }

    [Fact]
    public void TimePeriod_24hIsActive()
    {
        var cut = Render<AdminMonitoring>();
        var active = cut.Find("[data-testid='mon-period-active']");
        Assert.Equal("24h", active.TextContent.Trim());
    }

    [Fact]
    public void D1Ribbon_HasFourTiles()
    {
        var cut = Render<AdminMonitoring>();
        // All four health-ribbon tiles are present by data-testid
        cut.Find("[data-testid='mon-tile-latency']");
        cut.Find("[data-testid='mon-tile-5xx']");
        cut.Find("[data-testid='mon-tile-cost']");
        cut.Find("[data-testid='mon-tile-refusal']");
    }

    [Fact]
    public void LatencyTile_Shows2310ms()
    {
        var cut = Render<AdminMonitoring>();
        var value = cut.Find("[data-testid='mon-tile-latency-value']");
        Assert.Contains("2,310", value.TextContent);
    }

    [Fact]
    public void LatencyTile_StateIsOk()
    {
        var cut = Render<AdminMonitoring>();
        var state = cut.Find("[data-testid='mon-tile-latency-state']");
        Assert.Contains("OK", state.TextContent);
    }

    [Fact]
    public void CostTile_IsEvalOnly()
    {
        var cut = Render<AdminMonitoring>();
        var state = cut.Find("[data-testid='mon-tile-cost-state']");
        Assert.Contains("Eval-only", state.TextContent);
    }

    [Fact]
    public void CostTile_Shows0Dollars()
    {
        var cut = Render<AdminMonitoring>();
        var value = cut.Find("[data-testid='mon-tile-cost-value']");
        Assert.Contains("$0.00", value.TextContent);
    }

    [Fact]
    public void RefusalTile_Shows62Percent()
    {
        var cut = Render<AdminMonitoring>();
        var value = cut.Find("[data-testid='mon-tile-refusal-value']");
        Assert.Contains("6.2", value.TextContent);
    }

    [Fact]
    public void D2_HasSixRefusalCategories()
    {
        var cut = Render<AdminMonitoring>();
        // Verify all six refusal categories render
        cut.Find("[data-testid='mon-refusal-outofscope']");
        cut.Find("[data-testid='mon-refusal-insufficientgrounding']");
        cut.Find("[data-testid='mon-refusal-nocitation']");
        cut.Find("[data-testid='mon-refusal-lowmodelconfidence']");
        cut.Find("[data-testid='mon-refusal-harmfulcontent']");
        cut.Find("[data-testid='mon-refusal-costceilinghit']");
    }

    [Fact]
    public void D3_HasFourPipelineRows()
    {
        var cut = Render<AdminMonitoring>();
        cut.Find("[data-testid='mon-pipeline-lease']");
        cut.Find("[data-testid='mon-pipeline-deadletter']");
        cut.Find("[data-testid='mon-pipeline-shortcircuit']");
        cut.Find("[data-testid='mon-pipeline-drift']");
    }

    [Fact]
    public void D3_DeadLetterRow_StateIsReview()
    {
        var cut = Render<AdminMonitoring>();
        var state = cut.Find("[data-testid='mon-pipeline-deadletter-state']");
        Assert.Equal("review", state.TextContent.Trim());
    }

    [Fact]
    public void D3_ReconcileDriftRow_HasCanaryClass()
    {
        var cut = Render<AdminMonitoring>();
        var driftRow = cut.Find("[data-testid='mon-pipeline-drift']");
        Assert.Contains("mon-pipeline__row--canary", driftRow.ClassName);
    }

    [Fact]
    public void D4_HasFiveAlertRows()
    {
        var cut = Render<AdminMonitoring>();
        cut.Find("[data-testid='mon-alert-latency']");
        cut.Find("[data-testid='mon-alert-5xx']");
        cut.Find("[data-testid='mon-alert-cost']");
        cut.Find("[data-testid='mon-alert-deadletter']");
        cut.Find("[data-testid='mon-alert-availability']");
    }

    [Fact]
    public void D4_DailyCostAlert_IsSuppressed()
    {
        var cut = Render<AdminMonitoring>();
        var state = cut.Find("[data-testid='mon-alert-cost-state']");
        Assert.Equal("Suppressed", state.TextContent.Trim());
    }

    [Fact]
    public void D4_OtherAlerts_AreOk()
    {
        var cut = Render<AdminMonitoring>();
        foreach (var testid in new[]
        {
            "mon-alert-latency-state",
            "mon-alert-5xx-state",
            "mon-alert-deadletter-state",
            "mon-alert-availability-state",
        })
        {
            var el = cut.Find($"[data-testid='{testid}']");
            Assert.Equal("OK", el.TextContent.Trim());
        }
    }
}
