using MudBlazor;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class SourceStatusViewTests
{
    [Theory]
    [InlineData(true, null, SourceStatus.Active, "Active")]
    [InlineData(true, "Active", SourceStatus.Active, "Active")]
    [InlineData(false, "NoSource", SourceStatus.NoSource, "No source")]
    [InlineData(false, "Deferred", SourceStatus.Deferred, "Deferred")]
    [InlineData(false, null, SourceStatus.Disabled, "Disabled")]
    [InlineData(false, "Active", SourceStatus.Disabled, "Disabled")]
    public void Derive_MapsStatusAndLabel(
        bool enabled, string? discoveryStatus, SourceStatus expectedStatus, string expectedLabel)
    {
        var view = SourceStatusView.Derive(enabled, discoveryStatus);

        Assert.Equal(expectedStatus, view.Status);
        Assert.Equal(expectedLabel, view.Label);
        Assert.False(string.IsNullOrWhiteSpace(view.Icon)); // icon always present (colour not sole carrier)
    }

    [Fact]
    public void Derive_Active_UsesSuccessColour()
    {
        Assert.Equal(Color.Success, SourceStatusView.Derive(true, null).Color);
    }

    [Fact]
    public void Derive_Deferred_UsesWarningColour()
    {
        Assert.Equal(Color.Warning, SourceStatusView.Derive(false, "Deferred").Color);
    }

    [Fact]
    public void Derive_NoSource_UsesDefaultColour()
    {
        Assert.Equal(Color.Default, SourceStatusView.Derive(false, "NoSource").Color);
    }

    [Fact]
    public void Derive_Disabled_UsesDefaultColour()
    {
        Assert.Equal(Color.Default, SourceStatusView.Derive(false, null).Color);
    }
}
