using Bunit;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AdminCountValueTests : AsyncBunitContext
{
    public AdminCountValueTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void CommaFormatsLargeCount()
    {
        var cut = Render<AdminCountValue>(p => p
            .Add(x => x.TestId, "c")
            .Add(x => x.Loading, false)
            .Add(x => x.Failed, false)
            .Add(x => x.Count, 30875));
        Assert.Contains("30,875", cut.Find("[data-testid='c']").TextContent);
    }
}
