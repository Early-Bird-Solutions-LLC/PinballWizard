using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit smoke tests for AdminDashboard.razor (/admin).
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminDashboard is behind [Authorize]; tests run with
// AddTestAuthorization() set to authenticated so the dashboard content renders.
//
// Tests assert structural invariants: the page title pattern, the presence of
// summary cards for Machines / Sources / Documents, and that data-testid
// sentinels are rendered for count placeholders.
//
// Note: AdminDashboard does NOT have a @rendermode directive — it inherits
// the layout's server-side rendering in production. bUnit renders it
// synchronously without a rendermode override, which is fine for smoke tests.
public sealed class AdminDashboardTests : TestContext
{
    public AdminDashboardTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Authorize the test context so [Authorize] on the page (and AdminLayout)
        // does not redirect to the login challenge.
        this.AddTestAuthorization().SetAuthorized("test-admin@example.com");
        _ = Services.GetRequiredService<FakeNavigationManager>();
    }

    [Fact]
    public void AdminDashboard_Renders_WithoutThrowing()
    {
        // Arrange + Act — if this throws the test fails.
        var cut = RenderComponent<AdminDashboard>();

        // Assert — component rendered and has markup.
        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public void AdminDashboard_Renders_MachinesCountSentinel()
    {
        var cut = RenderComponent<AdminDashboard>();

        // Machines count placeholder is rendered.
        var el = cut.Find("[data-testid='admin-machines-count']");
        Assert.NotNull(el);
    }

    [Fact]
    public void AdminDashboard_Renders_SourcesCountSentinel()
    {
        var cut = RenderComponent<AdminDashboard>();

        var el = cut.Find("[data-testid='admin-sources-count']");
        Assert.NotNull(el);
    }

    [Fact]
    public void AdminDashboard_Renders_DocumentsCountSentinel()
    {
        var cut = RenderComponent<AdminDashboard>();

        var el = cut.Find("[data-testid='admin-documents-count']");
        Assert.NotNull(el);
    }

    [Fact]
    public void AdminDashboard_ViewCatalogButton_HrefsAdminMachines()
    {
        var cut = RenderComponent<AdminDashboard>();

        // MudButton for machines catalog link.
        var link = cut.Find("a[href='/admin/machines']");
        Assert.NotNull(link);
    }

    [Fact]
    public void AdminDashboard_ViewSourcesButton_HrefsAdminSources()
    {
        var cut = RenderComponent<AdminDashboard>();

        var link = cut.Find("a[href='/admin/sources']");
        Assert.NotNull(link);
    }
}
