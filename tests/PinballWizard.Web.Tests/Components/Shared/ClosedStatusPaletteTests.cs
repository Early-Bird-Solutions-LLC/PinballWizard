using System;
using MudBlazor;
using PinballWizard.Application.Catalog;
using PinballWizard.Web.Components.Pages.Admin;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

// Guards the closed 5-role palette (design §4.1): status colors are only
// Success / Error / Default. Amber (Primary/Warning), blue (Info) and teal
// (Tertiary) are interactive-only or banned and must never be a status color.
public sealed class ClosedStatusPaletteTests
{
    private static readonly Color[] Allowed = { Color.Success, Color.Error, Color.Default };

    [Fact]
    public void DocumentLinkStatusColor_OnlyEmitsAllowedColors()
    {
        string?[] inputs =
        {
            "linked", "Linked", "manually_linked", "failed", "Failed",
            "not_in_catalog", "NotInCatalog", "platform_generic", "PlatformGeneric",
            "unknown", null,
        };
        Assert.All(inputs, s => Assert.Contains(DocumentLinkStatusColor.For(s), Allowed));
    }

    [Fact]
    public void JobStatusColor_OnlyEmitsAllowedColors()
    {
        string[] inputs = { "Succeeded", "Running", "Processing", "Failed", "Degraded", "Stopped", "Queued" };
        Assert.All(inputs, s => Assert.Contains(JobStatusColor.For(s), Allowed));
    }

    [Fact]
    public void CatalogHealthColors_OnlyEmitsAllowedColors()
    {
        Assert.All(
            Enum.GetValues<CatalogHealthFlag>(),
            f => Assert.Contains(CatalogHealthColors.ForFlag(f), Allowed));
    }

    [Fact]
    public void SourceStatusView_OnlyEmitsAllowedColors()
    {
        var views = new[]
        {
            SourceStatusView.Derive(true, null),
            SourceStatusView.Derive(false, "NoSource"),
            SourceStatusView.Derive(false, "Deferred"),
            SourceStatusView.Derive(false, null),
        };
        Assert.All(views, v => Assert.Contains(v.Color, Allowed));
    }
}
