using Bunit;
using MudBlazor.Services;
using PinballWizard.Application.Catalog;
using PinballWizard.Web.Components.Pages.Admin;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class CatalogHealthLegendTests : AsyncBunitContext
{
    public CatalogHealthLegendTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersAllFlagNamesAndDescriptions()
    {
        var cut = RenderWithPopover<CatalogHealthLegend>();
        var markup = cut.Markup;

        // All four flag names must appear as chip labels.
        foreach (var flag in Enum.GetValues<CatalogHealthFlag>())
        {
            Assert.Contains(flag.ToString(), markup);
        }

        // All four description strings must appear as inline caption text.
        foreach (var flag in Enum.GetValues<CatalogHealthFlag>())
        {
            Assert.Contains(CatalogHealthColors.Describe(flag), markup);
        }
    }

    [Fact]
    public void HasDataTestId()
    {
        var cut = RenderWithPopover<CatalogHealthLegend>();
        cut.Find("[data-testid='catalog-health-legend']");
    }
}
