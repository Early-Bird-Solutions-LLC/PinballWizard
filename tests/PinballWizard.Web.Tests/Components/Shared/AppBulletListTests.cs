using Bunit;
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

    // Renders as a native semantic <ul>, not a MudList. MudList emits an
    // interactive role="listbox" that axe flags on static content
    // (aria-input-field-name + nested-interactive). A plain <ul> is the
    // accessible, correct markup for a static bullet list — the same pattern
    // the /engineering "Latest ADRs" list uses.
    [Fact]
    public void RendersNativeUnorderedList()
    {
        var cut = Render<AppBulletList>(p => p
            .AddChildContent<AppBulletItem>(bp => bp.AddChildContent("Item")));

        _ = cut.Find("ul.app-bullet-list");
    }

    [Fact]
    public void RendersEachItemAsNativeListItem()
    {
        var cut = Render<AppBulletList>(p => p
            .AddChildContent<AppBulletItem>(bp => bp.AddChildContent("One"))
            .AddChildContent<AppBulletItem>(bp => bp.AddChildContent("Two")));

        Assert.Equal(2, cut.FindAll("li.app-bullet-item").Count);
    }

    // a11y regression guard: the reason this component is native <ul>/<li> and
    // not MudList/MudListItem is to keep interactive ARIA roles off static
    // content. Reverting to MudList would reintroduce the axe violation this
    // fails on.
    [Fact]
    public void DoesNotEmitInteractiveListRoles()
    {
        var cut = Render<AppBulletList>(p => p
            .AddChildContent<AppBulletItem>(bp => bp.AddChildContent("Item")));

        Assert.DoesNotContain("role=\"listbox\"", cut.Markup);
        Assert.DoesNotContain("role=\"option\"", cut.Markup);
    }

    // Class and arbitrary attributes (e.g. data-testid) splat onto the <ul>,
    // preserving the call-site hooks the public pages rely on.
    [Fact]
    public void ForwardsClassAndAttributesToTheList()
    {
        var cut = Render<AppBulletList>(p => p
            .Add(x => x.Class, "about-tech-list")
            .AddUnmatched("data-testid", "my-list")
            .AddChildContent<AppBulletItem>(bp => bp.AddChildContent("Item")));

        var ul = cut.Find("ul.app-bullet-list");
        Assert.Contains("about-tech-list", ul.GetAttribute("class"));
        Assert.Equal("my-list", ul.GetAttribute("data-testid"));
    }
}
