using Bunit;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppStatusChipTests : AsyncBunitContext
{
    public AppStatusChipTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersLabel()
    {
        var cut = Render<AppStatusChip>(p => p
            .Add(x => x.Color, Color.Success)
            .AddChildContent("Enabled"));
        Assert.Contains("Enabled", cut.Markup);
    }

    [Fact]
    public void DefaultsToVariantFilled()
    {
        var cut = Render<AppStatusChip>(p => p
            .Add(x => x.Color, Color.Success)
            .AddChildContent("OK"));
        // MudChip Variant.Filled renders with mud-chip-filled class
        Assert.Contains("mud-chip-filled", cut.Find(".mud-chip").GetAttribute("class") ?? "");
    }

    [Fact]
    public void SplatsDataTestId()
    {
        var cut = Render<AppStatusChip>(p => p
            .Add(x => x.Color, Color.Error)
            .AddChildContent("Err")
            .AddUnmatched("data-testid", "chip-test"));
        cut.Find("[data-testid='chip-test']");
    }
}
