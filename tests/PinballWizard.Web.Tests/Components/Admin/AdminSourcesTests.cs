using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for AdminSources.razor (/admin/sources).
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminSources is behind [Authorize]; tests run with
// AddAuthorization() set to authenticated.
//
// The grid binds to an empty list, so the "No sources configured" empty-state
// is the expected render. Tests assert the grid sentinel and empty-state text
// are present — this is a behavioral assertion: "grid with empty data shows
// the empty state" fires the actual empty-state code path.
public sealed class AdminSourcesTests : AsyncBunitContext
{
    public AdminSourcesTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void AdminSources_Renders_WithoutThrowing()
    {
        var cut = RenderWithPopover<AdminSources>();

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public void AdminSources_Renders_DataGridSentinel()
    {
        var cut = RenderWithPopover<AdminSources>();

        // The MudDataGrid wrapper element carries data-testid.
        var grid = cut.Find("[data-testid='admin-sources-grid']");
        Assert.NotNull(grid);
    }

    [Fact]
    public void AdminSources_EmptyList_RendersNoSourcesConfiguredMessage()
    {
        var cut = RenderWithPopover<AdminSources>();

        // Behavioral assertion: empty-list path renders the "No sources configured"
        // empty-state content defined in <NoRecordsContent>.
        var empty = cut.Find("[data-testid='admin-sources-empty']");
        Assert.Contains("No sources configured", empty.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSources_Breadcrumb_ContainsAdminRoot()
    {
        var cut = RenderWithPopover<AdminSources>();

        // Breadcrumb trail includes a link back to /admin.
        var adminLink = cut.Find("a[href='/admin']");
        Assert.NotNull(adminLink);
    }
}
