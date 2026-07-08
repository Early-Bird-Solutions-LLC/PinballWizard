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

    [Fact]
    public void Describe_ReturnsNonEmptyStringForAllFlags()
    {
        foreach (var flag in Enum.GetValues<CatalogHealthFlag>())
        {
            Assert.False(
                string.IsNullOrEmpty(CatalogHealthColors.Describe(flag)),
                $"Describe({flag}) must return a non-empty string.");
        }
    }

    [Fact]
    public void Describe_ThrowsForUnmappedFlag()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CatalogHealthColors.Describe((CatalogHealthFlag)999));
    }

    [Theory]
    [InlineData(CatalogHealthFlag.Ok,         "Healthy — documents present, including a manual.")]
    [InlineData(CatalogHealthFlag.Empty,      "No documents linked to this machine yet.")]
    [InlineData(CatalogHealthFlag.NoManual,   "Has documents, but no manual.")]
    [InlineData(CatalogHealthFlag.EditionGap, "Another edition of this game has more documents — this edition may be under-covered.")]
    public void Describe_ReturnsExpectedString(CatalogHealthFlag flag, string expected)
    {
        Assert.Equal(expected, CatalogHealthColors.Describe(flag));
    }
}
