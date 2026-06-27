using Bunit;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppErrorAlertTests : AsyncBunitContext
{
    public AppErrorAlertTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersChildContent()
    {
        var cut = Render<AppErrorAlert>(p => p
            .AddChildContent("Something went wrong."));
        Assert.Contains("Something went wrong.", cut.Markup);
    }

    [Fact]
    public void DefaultClassIsMb4()
    {
        var cut = Render<AppErrorAlert>(p => p
            .AddChildContent("err"));
        Assert.Contains("mb-4", cut.Find(".mud-alert").GetAttribute("class"));
    }

    [Fact]
    public void SplatsDataTestId()
    {
        var cut = Render<AppErrorAlert>(p => p
            .AddChildContent("err")
            .AddUnmatched("data-testid", "my-alert"));
        cut.Find("[data-testid='my-alert']");
    }
}
