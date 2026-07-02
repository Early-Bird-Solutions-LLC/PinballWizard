using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

public sealed class AppNavRailTests : AsyncBunitContext
{
    private static readonly IReadOnlyList<NavRailItem> SampleItems = new[]
    {
        new NavRailItem("/", "Ask the Wizard", Icons.Material.Filled.AutoAwesome, MatchAll: true),
        new NavRailItem("/about", "What we cover", Icons.Material.Filled.Explore),
    };

    public AppNavRailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void RendersOneNavLinkPerItem_WithCorrectHref()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        Assert.NotNull(cut.Find("a[href='/']"));
        Assert.NotNull(cut.Find("a[href='/about']"));
    }

    [Fact]
    public void InitialState_IsCollapsed_WhenOpenFalse()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var toggle = cut.Find("[data-testid='nav-rail-toggle']");
        Assert.Equal("Expand navigation", toggle.GetAttribute("aria-label"));
    }

    [Fact]
    public void InitialState_IsExpanded_WhenOpenTrue()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var toggle = cut.Find("[data-testid='nav-rail-toggle']");
        Assert.Equal("Collapse navigation", toggle.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Toggle_FlipsState_OnClick()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='nav-rail-toggle']").Click());

        Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
    }

    [Fact]
    public void NavLink_UsesMatchAll_ForMatchAllItems()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), true);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var navLinks = cut.FindComponents<MudNavLink>();
        var rootLink = navLinks.Single(l => l.Instance.Href == "/");
        var aboutLink = navLinks.Single(l => l.Instance.Href == "/about");

        Assert.Equal(NavLinkMatch.All, rootLink.Instance.Match);
        Assert.Equal(NavLinkMatch.Prefix, aboutLink.Instance.Match);
    }
}
