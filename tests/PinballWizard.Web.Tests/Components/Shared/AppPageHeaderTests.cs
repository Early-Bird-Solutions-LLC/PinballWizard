using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppPageHeaderTests : AsyncBunitContext
{
    public AppPageHeaderTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersTitle()
    {
        var cut = Render<AppPageHeader>(p => p
            .Add(x => x.Title, "My Page"));
        Assert.Contains("My Page", cut.Markup);
    }

    [Fact]
    public void RendersSubtitle_AsBody2()
    {
        var cut = Render<AppPageHeader>(p => p
            .Add(x => x.Title, "My Page")
            .Add(x => x.Subtitle, "A helpful description"));
        var markup = cut.Markup;
        Assert.Contains("A helpful description", markup);
        // Subtitle must be body2 (mud-typography-body2 class)
        Assert.Contains("mud-typography-body2", markup);
    }

    [Fact]
    public void OmitsSubtitleWhenNull()
    {
        var cut = Render<AppPageHeader>(p => p
            .Add(x => x.Title, "My Page"));
        Assert.Empty(cut.FindAll(".mud-typography-body2"));
    }

    [Fact]
    public void RendersBreadcrumbsWhenProvided()
    {
        var crumbs = new List<BreadcrumbItem>
        {
            new("Admin", href: "/admin"),
            new("Sources", href: "/admin/sources"),
        };
        var cut = Render<AppPageHeader>(p => p
            .Add(x => x.Title, "Sources")
            .Add(x => x.Breadcrumbs, crumbs));
        cut.Find(".mud-breadcrumbs");
    }

    [Fact]
    public void OmitsBreadcrumbsWhenNull()
    {
        var cut = Render<AppPageHeader>(p => p
            .Add(x => x.Title, "My Page"));
        Assert.Empty(cut.FindAll(".mud-breadcrumbs"));
    }

    [Fact]
    public void RendersActionsSlot()
    {
        RenderFragment actions = b =>
        {
            b.OpenElement(0, "button");
            b.AddAttribute(1, "data-testid", "action-btn");
            b.AddContent(2, "New");
            b.CloseElement();
        };
        var cut = Render<AppPageHeader>(p => p
            .Add(x => x.Title, "My Page")
            .Add(x => x.Actions, actions));
        cut.Find("[data-testid='action-btn']");
    }
}
