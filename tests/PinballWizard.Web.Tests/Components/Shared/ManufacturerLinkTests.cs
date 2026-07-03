using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

// ManufacturerLink centralizes the /manufacturers/{key} URL shape used by every
// call-site (ADR-0046 shared-component doctrine). It renders a MudLink whose href
// is the manufacturer detail route and whose text is the display name.
public sealed class ManufacturerLinkTests : AsyncBunitContext
{
    public ManufacturerLinkTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Renders_LinkToDetailRoute_WithDisplayNameText()
    {
        var cut = Render<ManufacturerLink>(p => p
            .Add(x => x.ManufacturerKey, "stern")
            .Add(x => x.DisplayName, "Stern Pinball"));

        var anchor = cut.Find("a[href='/manufacturers/stern']");
        Assert.Contains("Stern Pinball", anchor.TextContent, StringComparison.Ordinal);
    }
}
