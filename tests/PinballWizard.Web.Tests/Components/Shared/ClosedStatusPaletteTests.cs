using System;
using System.Linq;
using MudBlazor;
using PinballWizard.Application.Catalog;
using PinballWizard.Core.Models;
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

    // Reflects over LinkStatus rather than enumerating strings: a hardcoded input
    // list silently exempts every status added after it was written (which is how
    // NeedsReview → Color.Info escaped this guard). Both accepted forms are covered
    // — PascalCase (LinkStatus.ToString()) and the snake_case Cosmos wire form.
    [Fact]
    public void DocumentLinkStatusColor_OnlyEmitsAllowedColors()
    {
        foreach (var status in Enum.GetValues<LinkStatus>())
        {
            var pascal = status.ToString();
            var snake = ToSnakeCase(pascal);

            Assert.Contains(DocumentLinkStatusColor.For(pascal), Allowed);
            Assert.Contains(DocumentLinkStatusColor.For(snake), Allowed);
        }

        // Non-status and unmapped inputs must also stay inside the palette.
        string?[] extras = { "unknown", "", null };
        Assert.All(extras, s => Assert.Contains(DocumentLinkStatusColor.For(s), Allowed));
    }

    // "NotInCatalog" -> "not_in_catalog"; mirrors the mapper in
    // CosmosRawDocumentRepository.ToWireStatus.
    private static string ToSnakeCase(string pascal) =>
        string.Concat(pascal.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

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
