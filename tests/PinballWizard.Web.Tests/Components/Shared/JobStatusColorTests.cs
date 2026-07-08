using MudBlazor;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class JobStatusColorTests
{
    [Theory]
    [InlineData("Succeeded", "Success")]
    [InlineData("Running", "Success")]     // active → green (was Info/blue)
    [InlineData("Processing", "Success")]  // active → green (was Info/blue)
    [InlineData("Failed", "Error")]
    [InlineData("Degraded", "Error")]      // problem → red (was Warning/amber)
    [InlineData("Stopped", "Error")]       // per spec §4.2 default (reviewer may switch to Default)
    [InlineData("Queued", "Default")]
    public void For_MapsToClosedPalette(string status, string expectedColor)
    {
        Assert.Equal(expectedColor, JobStatusColor.For(status).ToString());
    }
}
