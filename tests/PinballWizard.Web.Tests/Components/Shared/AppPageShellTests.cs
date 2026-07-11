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
        // AppPageHeader renders the title as Typo.h4.
        cut.Find(".mud-typography-h4");
    }

    [Fact]
    public void OmitsHeader_WhenTitleNull()
    {
        // Data-dependent-header pages (e.g. /manufacturers/{key}) omit Title and
        // render their own header inside ChildContent — the shell must not inject one.
        var cut = Render<AppPageShell>(p => p
            .Add(x => x.ChildContent, Body("content")));

        Assert.Empty(cut.FindAll(".mud-typography-h4"));
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
