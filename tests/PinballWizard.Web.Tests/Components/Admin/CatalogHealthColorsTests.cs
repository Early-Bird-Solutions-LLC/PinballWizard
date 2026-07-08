using MudBlazor;
using PinballWizard.Application.Catalog;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class CatalogHealthColorsTests
{
    [Theory]
    [InlineData(CatalogHealthFlag.Empty, "Error")]
    [InlineData(CatalogHealthFlag.NoManual, "Default")]   // informational → neutral (was amber)
    [InlineData(CatalogHealthFlag.EditionGap, "Default")] // informational → neutral (was amber)
    [InlineData(CatalogHealthFlag.Ok, "Success")]
    public void ForFlag_MapsToClosedPalette(CatalogHealthFlag flag, string expectedColor)
    {
        Assert.Equal(expectedColor, CatalogHealthColors.ForFlag(flag).ToString());
    }
}
