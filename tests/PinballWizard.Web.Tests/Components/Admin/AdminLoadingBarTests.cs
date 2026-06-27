using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminLoadingBar.razor — the shared /admin indeterminate
// progress bar extracted from the five admin pages.
//
// Behavior under test (not structure): every rendered bar carries an accessible
// name (the WCAG aria-progressbar-name contract the PR #450 fix established and
// this extraction makes structural), caller-supplied attributes splat through to
// the bar, and a blank Label is rejected rather than silently rendering a
// nameless progressbar — the exact a11y defect the component exists to prevent.
public sealed class AdminLoadingBarTests : AsyncBunitContext
{
    public AdminLoadingBarTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Renders_ProgressBar_WithAccessibleNameFromLabel()
    {
        var cut = Render<AdminLoadingBar>(ps => ps.Add(p => p.Label, "Loading machine catalog"));

        var bar = cut.Find("[role=progressbar]");
        Assert.Equal("Loading machine catalog", bar.GetAttribute("aria-label"));
    }

    [Fact]
    public void ForwardsExtraAttributes_AlongsideAccessibleName()
    {
        // AdminSettings passes a data-testid; it must reach the bar without
        // displacing the aria-label (which is written last, so it always wins).
        var cut = Render<AdminLoadingBar>(ps => ps
            .Add(p => p.Label, "Loading settings")
            .AddUnmatched("data-testid", "settings-loading"));

        var bar = cut.Find("[role=progressbar]");
        Assert.Equal("settings-loading", bar.GetAttribute("data-testid"));
        Assert.Equal("Loading settings", bar.GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Throws_WhenLabelBlank(string label)
    {
        // A blank accessible name is a nameless progressbar — the a11y regression
        // this component guards against. Reject it at render rather than ship it.
        Assert.Throws<ArgumentException>(() =>
            Render<AdminLoadingBar>(ps => ps.Add(p => p.Label, label)));
    }
}
