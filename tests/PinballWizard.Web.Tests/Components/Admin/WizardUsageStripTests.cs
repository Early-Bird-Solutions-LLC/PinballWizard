using Bunit;
using PinballWizard.Web.Components.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for WizardUsageStrip.razor.
//
// Hardcoded showcase stats strip on the admin dashboard. No DI, no parameters —
// tests assert the key data points and structure render correctly.
public sealed class WizardUsageStripTests : AsyncBunitContext
{
    [Fact]
    public void Renders_WithoutException()
    {
        _ = Render<WizardUsageStrip>();
    }

    [Fact]
    public void Strip_HasDataTestId()
    {
        var cut = Render<WizardUsageStrip>();
        cut.Find("[data-testid='wizard-usage-strip']");
    }

    [Fact]
    public void Headline_Shows1652()
    {
        var cut = Render<WizardUsageStrip>();
        var headline = cut.Find("[data-testid='wizard-usage-headline']");
        Assert.Equal("1,652", headline.TextContent.Trim());
    }

    [Fact]
    public void GroundedStat_Shows938Percent()
    {
        var cut = Render<WizardUsageStrip>();
        var grounded = cut.Find("[data-testid='wizard-usage-grounded']");
        Assert.Equal("93.8%", grounded.TextContent.Trim());
    }

    [Fact]
    public void RefusedStat_Shows62Percent()
    {
        var cut = Render<WizardUsageStrip>();
        var refused = cut.Find("[data-testid='wizard-usage-refused']");
        Assert.Equal("6.2%", refused.TextContent.Trim());
    }

    [Fact]
    public void LatencyStat_Shows23s()
    {
        var cut = Render<WizardUsageStrip>();
        var latency = cut.Find("[data-testid='wizard-usage-latency']");
        Assert.Equal("2.3s", latency.TextContent.Trim());
    }
}
