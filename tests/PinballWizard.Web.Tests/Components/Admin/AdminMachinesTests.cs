using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for AdminMachines.razor (/admin/machines).
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminMachines is behind [Authorize]; tests run with
// AddAuthorization() set to authenticated.
//
// The grid binds to an empty list, so the "No machines in catalog" empty-state
// is the expected render. Tests assert behavioral invariants: empty-data path
// shows the empty state, data-testid sentinels are present, breadcrumbs link
// back to the admin root.
public sealed class AdminMachinesTests : AsyncBunitContext
{
    public AdminMachinesTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void AdminMachines_Renders_WithoutThrowing()
    {
        var cut = Render<AdminMachines>();

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public void AdminMachines_Renders_DataGridSentinel()
    {
        var cut = Render<AdminMachines>();

        var grid = cut.Find("[data-testid='admin-machines-grid']");
        Assert.NotNull(grid);
    }

    [Fact]
    public void AdminMachines_EmptyList_RendersNoMachinesInCatalogMessage()
    {
        var cut = Render<AdminMachines>();

        // Behavioral assertion: empty-list path renders the "No machines in catalog"
        // empty-state content defined in <NoRecordsContent>.
        var empty = cut.Find("[data-testid='admin-machines-empty']");
        Assert.Contains("No machines in catalog", empty.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminMachines_Breadcrumb_ContainsAdminRoot()
    {
        var cut = Render<AdminMachines>();

        // Breadcrumb trail includes a link back to /admin.
        var adminLink = cut.Find("a[href='/admin']");
        Assert.NotNull(adminLink);
    }
}
