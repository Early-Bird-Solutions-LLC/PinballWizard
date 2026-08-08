using Bunit;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests;

public class SearchBarTests : BunitContext, IAsyncLifetime
{
    public SearchBarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options =>
        {
            options.PopoverOptions.CheckForPopoverProvider = false;
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
    }

    [Fact]
    public void SearchBar_RendersWithPlaceholder()
    {
        var cut = Render<SearchBar>(parameters => parameters
            .Add(p => p.Placeholder, "Search pinball..."));

        Assert.Contains("Search pinball...", cut.Markup);
    }

    [Fact]
    public void SearchBar_RendersSearchIcon()
    {
        var cut = Render<SearchBar>();

        Assert.Contains("aria-label", cut.Markup);
    }

    [Fact]
    public void SearchBar_HasAriaLabel()
    {
        var cut = Render<SearchBar>();

        Assert.Contains("Search pinball knowledge base", cut.Markup);
    }
}
