using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppPageShellTests : AsyncBunitContext
{
    public AppPageShellTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static RenderFragment Body(string text) => b =>
    {
        b.OpenElement(0, "div");
        b.AddAttribute(1, "data-testid", "shell-body");
        b.AddContent(2, text);
        b.CloseElement();
    };

    [Fact]
    public void WrapsChildContentInMudContainer()
    {
        var cut = Render<AppPageShell>(p => p
            .Add(x => x.ChildContent, Body("content")));

        cut.Find(".mud-container");
        var body = cut.Find("[data-testid='shell-body']");
        Assert.Equal("content", body.TextContent);
    }

    [Fact]
    public void RendersHeader_WhenTitleSet()
    {
        var cut = Render<AppPageShell>(p => p
            .Add(x => x.Title, "Machine Catalog")
            .Add(x => x.Subtitle, "All machines")
            .Add(x => x.ChildContent, Body("content")));

        Assert.Contains("Machine Catalog", cut.Markup);
        // The title is the page's <h1>. It was Typo.h4, which left every page using
        // this shell without an <h1> (#790); assert the element, not just the
        // typography class, so a future restyle cannot quietly drop the semantics.
        Assert.Equal("Machine Catalog", cut.Find("h1").TextContent.Trim());
    }

    [Fact]
    public void RendersHiddenHeading_WhenTitleNull()
    {
        // Detail pages render no visible title bar, and their header often lives inside
        // a loaded-branch @if - so before this the loading / not-found / load-failed
        // states carried no heading at all and axe's page-has-heading-one failed on
        // every one (#790). The shell now guarantees exactly one <h1> in every state.
        var cut = Render<AppPageShell>(p => p
            .Add(x => x.AccessibleTitle, "Source detail")
            .Add(x => x.ChildContent, Body("content")));

        var h1 = cut.Find("h1");
        Assert.Equal("Source detail", h1.TextContent.Trim());
        Assert.Contains("pw-visually-hidden", h1.ClassList);
    }

    [Fact]
    public void HiddenHeading_FallsBackToLastBreadcrumb()
    {
        // The final breadcrumb names the page and is already supplied in every state,
        // so it is the natural default and saves every detail page restating its title.
        var cut = Render<AppPageShell>(p => p
            .Add(x => x.Breadcrumbs, new List<BreadcrumbItem>
            {
                new("Admin", "/admin"),
                new("Machines", "/admin/machines"),
                new("Godzilla Pro", null, disabled: true),
            })
            .Add(x => x.ChildContent, Body("content")));

        Assert.Equal("Godzilla Pro", cut.Find("h1").TextContent.Trim());
    }

    [Fact]
    public void OmitsHeader_WhenTitleNull()
    {
        // Data-dependent-header pages (e.g. /manufacturers/{key}) omit Title and
        // render their own header inside ChildContent — the shell must not inject one.
        var cut = Render<AppPageShell>(p => p
            .Add(x => x.ChildContent, Body("content")));

        // No VISIBLE header - but the shell still owes the document an <h1>, so with
        // neither Title nor AccessibleTitle nor Breadcrumbs there is simply nothing to
        // render one from. That case is covered by RendersHiddenHeading_WhenTitleNull.
        Assert.Empty(cut.FindAll(".pw-page-title"));
        cut.Find("[data-testid='shell-body']");
    }

    [Fact]
    public void RendersBreadcrumbs_WhenTitleNull()
    {
        // Detail pages whose heading depends on loaded data (/admin/machines/{id},
        // /admin/jobs/{job}/executions/{exec}) pass Breadcrumbs with no Title, so the
        // trail survives the loading / not-found / load-failed branches a post-load
        // header never reaches. Before this, Breadcrumbs was only forwarded to
        // AppPageHeader — which the shell skips when Title is null — so a supplied
        // Breadcrumbs was silently swallowed and those pages hand-rolled MudBreadcrumbs.
        var crumbs = new List<BreadcrumbItem>
        {
            new("Admin", "/admin"),
            new("Machines", "/admin/machines"),
        };

        var cut = Render<AppPageShell>(p => p
            .Add(x => x.Breadcrumbs, crumbs)
            .Add(x => x.ChildContent, Body("content")));

        // The trail renders...
        Assert.Contains("/admin/machines", cut.Markup);
        Assert.Contains("Machines", cut.Markup);
        // ...without the shell injecting a heading it has no title for.
        Assert.Empty(cut.FindAll(".mud-typography-h4"));
    }

    [Fact]
    public void RendersActions_WhenTitleSet()
    {
        // The Actions slot threads through to AppPageHeader — guards the pass-through
        // wiring (AdminLinkOverrides is the only consumer and has no shell-level test).
        RenderFragment actions = b =>
        {
            b.OpenElement(0, "button");
            b.AddAttribute(1, "data-testid", "shell-action");
            b.AddContent(2, "New");
            b.CloseElement();
        };
        var cut = Render<AppPageShell>(p => p
            .Add(x => x.Title, "Link Overrides")
            .Add(x => x.Actions, actions)
            .Add(x => x.ChildContent, Body("content")));

        cut.Find("[data-testid='shell-action']");
    }
}
