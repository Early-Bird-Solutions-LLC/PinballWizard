using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppSummaryCardTests : AsyncBunitContext
{
    public AppSummaryCardTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RendersLabelAndActionLink()
    {
        var cut = Render<AppSummaryCard>(p => p
            .Add(x => x.Icon, Icons.Material.Filled.SportsBaseball)
            .Add(x => x.IconColor, Color.Primary)
            .Add(x => x.Label, "Machines")
            .Add(x => x.ActionHref, "/admin/machines")
            .Add(x => x.ActionLabel, "View catalog")
            .Add(x => x.Content, (RenderFragment)(b =>
            {
                b.OpenElement(0, "span");
                b.AddContent(1, "42");
                b.CloseElement();
            })));
        var markup = cut.Markup;
        Assert.Contains("Machines", markup);
        Assert.Contains("/admin/machines", markup);
        Assert.Contains("View catalog", markup);
        Assert.Contains("42", markup);
    }

    [Fact]
    public void RendersCaptionWhenProvided()
    {
        var cut = Render<AppSummaryCard>(p => p
            .Add(x => x.Icon, Icons.Material.Filled.Storage)
            .Add(x => x.IconColor, Color.Info)
            .Add(x => x.Label, "RAG Corpus")
            .Add(x => x.Caption, "indexed chunk stats")
            .Add(x => x.ActionHref, "/admin/corpus")
            .Add(x => x.ActionLabel, "View corpus")
            .Add(x => x.Content, (RenderFragment)(b => b.AddContent(0, ""))));
        Assert.Contains("indexed chunk stats", cut.Markup);
    }

    [Fact]
    public void OmitsCaptionWhenNull()
    {
        var cut = Render<AppSummaryCard>(p => p
            .Add(x => x.Icon, Icons.Material.Filled.Factory)
            .Add(x => x.IconColor, Color.Primary)
            .Add(x => x.Label, "Manufacturers")
            .Add(x => x.ActionHref, "/admin/manufacturers")
            .Add(x => x.ActionLabel, "View manufacturers")
            .Add(x => x.Content, (RenderFragment)(b => b.AddContent(0, ""))));
        Assert.DoesNotContain("mud-typography-caption", cut.Markup);
    }

    [Fact]
    public void Cta_IsAmberPrimary_EvenWhenIconColorIsNot()
    {
        var cut = Render<AppSummaryCard>(p => p
            .Add(x => x.Icon, Icons.Material.Filled.Storage)
            .Add(x => x.IconColor, Color.Info)          // deliberately non-amber icon
            .Add(x => x.Label, "Documents Indexed")
            .Add(x => x.ActionHref, "/admin/corpus")
            .Add(x => x.ActionLabel, "View corpus")
            .Add(x => x.Content, b => b.AddMarkupContent(0, "<span>42</span>")));

        // The CTA button carries the primary (amber) text-color class, not info/blue.
        var button = cut.Find("a.mud-button-root");
        var cls = button.GetAttribute("class") ?? "";
        Assert.Contains("mud-button-text-primary", cls);
        Assert.DoesNotContain("mud-button-text-info", cls);
    }
}
