using Bunit;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppBulletListTests : AsyncBunitContext
{
    public AppBulletListTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersItems()
    {
        var cut = Render<AppBulletList>(p => p
            .AddChildContent<AppBulletItem>(bp => bp
                .AddChildContent("Item one")));
        Assert.Contains("Item one", cut.Markup);
    }

    [Fact]
    public void DefaultIconIsCircle()
    {
        var cut = Render<AppBulletList>(p => p
            .AddChildContent<AppBulletItem>(bp => bp
                .AddChildContent("Item")));
        Assert.Contains(Icons.Material.Filled.Circle, cut.Markup);
    }

    [Fact]
    public void AcceptsCustomIcon()
    {
        var cut = Render<AppBulletList>(p => p
            .AddChildContent<AppBulletItem>(bp => bp
                .Add(x => x.Icon, Icons.Material.Filled.Star)
                .AddChildContent("Item")));
        Assert.Contains(Icons.Material.Filled.Star, cut.Markup);
    }
}
