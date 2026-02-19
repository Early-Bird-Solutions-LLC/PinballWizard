using Bunit;
using MudBlazor.Services;
using Xunit;
using Index = PinballWizard.Web.Pages.Index;

namespace PinballWizard.Web.Tests;

public class WebSmokeTests : BunitContext
{
    [Fact]
    public void IndexPage_Renders_BrandTitle()
    {
        Services.AddMudServices();

        var cut = Render<Index>();

        Assert.Contains("PinWiz.ai", cut.Markup);
    }
}
