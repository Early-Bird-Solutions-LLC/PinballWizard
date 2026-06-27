using Bunit;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppEmptyStateTests : AsyncBunitContext
{
    public AppEmptyStateTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersHeading()
    {
        var cut = Render<AppEmptyState>(p => p
            .Add(x => x.Heading, "No items found"));
        Assert.Contains("No items found", cut.Markup);
    }

    [Fact]
    public void RendersDetailWhenProvided()
    {
        var cut = Render<AppEmptyState>(p => p
            .Add(x => x.Heading, "Empty")
            .Add(x => x.Detail, "Run the scraper first."));
        Assert.Contains("Run the scraper first.", cut.Markup);
    }

    [Fact]
    public void OmitsDetailWhenNull()
    {
        var cut = Render<AppEmptyState>(p => p
            .Add(x => x.Heading, "Empty"));
        Assert.DoesNotContain("null", cut.Markup);
    }

    [Fact]
    public void UsesInboxIconByDefault()
    {
        var cut = Render<AppEmptyState>(p => p
            .Add(x => x.Heading, "Empty"));
        Assert.Contains(Icons.Material.Outlined.Inbox, cut.Markup);
    }

    [Fact]
    public void AcceptsCustomIcon()
    {
        var cut = Render<AppEmptyState>(p => p
            .Add(x => x.Heading, "Empty")
            .Add(x => x.Icon, Icons.Material.Outlined.CheckCircle));
        Assert.Contains(Icons.Material.Outlined.CheckCircle, cut.Markup);
    }

    [Fact]
    public void SplatsDataTestId()
    {
        var cut = Render<AppEmptyState>(p => p
            .Add(x => x.Heading, "Empty")
            .AddUnmatched("data-testid", "my-empty"));
        cut.Find("[data-testid='my-empty']");
    }
}
