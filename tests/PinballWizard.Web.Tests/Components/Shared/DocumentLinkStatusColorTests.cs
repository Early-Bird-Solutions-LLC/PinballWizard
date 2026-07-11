using MudBlazor;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class DocumentLinkStatusColorTests
{
    [Theory]
    // success family — both casings
    [InlineData("linked", "Success")]
    [InlineData("Linked", "Success")]
    [InlineData("manually_linked", "Success")]
    [InlineData("ManuallyLinked", "Success")]
    // failure family — includes not-in-catalog (was amber on triage → must be red)
    [InlineData("failed", "Error")]
    [InlineData("Failed", "Error")]
    [InlineData("not_in_catalog", "Error")]
    [InlineData("NotInCatalog", "Error")]
    // non-status tag — platform_generic must be neutral (was amber/blue)
    [InlineData("platform_generic", "Default")]
    [InlineData("PlatformGeneric", "Default")]
    // unknown / null → neutral
    [InlineData("something_else", "Default")]
    [InlineData(null, "Default")]
    public void For_MapsToClosedPalette(string? status, string expectedColor)
    {
        Assert.Equal(expectedColor, DocumentLinkStatusColor.For(status).ToString());
    }
}
