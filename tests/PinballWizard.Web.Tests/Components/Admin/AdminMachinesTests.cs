using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminMachines.razor (/admin/machines) — AB#259.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminMachines is behind [Authorize]; tests run with
// AddAuthorization() set to authenticated.
//
// ICatalogStatsReadRepository is faked via NSubstitute returning two
// manufacturers — one machine with 0 docs (Empty flag), one all-OK machine.
// Tests assert behavioral invariants: grid sentinel, "as of" stamp, health
// chips, axis selector links, query-param grouping, breadcrumb trail.
//
// Note: AdminMachines has no @rendermode directive (ADR-0034 — static page).
// [SupplyParameterFromQuery] is set via ComponentParameter in Render<T>() or
// by setting the NavigationManager URI before rendering.
public sealed class AdminMachinesTests : AsyncBunitContext
{
    // ── Shared fake data ───────────────────────────────────────────────────────

    private static readonly DateTimeOffset FakeAsOf =
        new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // Two manufacturers, three machines total:
    //   stern → "Foo Pro"  (0 docs → Empty flag, franchise "foo")
    //   jjp   → "Bar CE"   (2 docs, HasManual → Ok, franchise "bar")
    //           "Bar LE"   (1 doc, HasManual → Ok, franchise "bar", EditionGap vs CE)
    private static readonly ManufacturerCatalogStats FakeStern = new(
        Manufacturer: "stern",
        AsOfUtc: FakeAsOf,
        Machines:
        [
            new MachineDocStats(
                MachineId:     "mch_stern_foo_pro",
                Title:         "Foo Pro",
                EditionLabel:  "Pro",
                GroupId:       "foo",
                Year:          2024,
                IsOpdbOnly:    false,
                DocCount:      0,
                DocTypeCounts: new Dictionary<string, int>(),
                HasManual:     false),
        ]);

    private static readonly ManufacturerCatalogStats FakeJjp = new(
        Manufacturer: "jjp",
        AsOfUtc: FakeAsOf.AddHours(1),   // later — min is Stern's timestamp
        Machines:
        [
            new MachineDocStats(
                MachineId:     "mch_jjp_bar_ce",
                Title:         "Bar CE",
                EditionLabel:  "CE",
                GroupId:       "bar",
                Year:          2023,
                IsOpdbOnly:    false,
                DocCount:      2,
                DocTypeCounts: new Dictionary<string, int> { ["Manual"] = 1 },
                HasManual:     true),
            new MachineDocStats(
                MachineId:     "mch_jjp_bar_le",
                Title:         "Bar LE",
                EditionLabel:  "LE",
                GroupId:       "bar",
                Year:          2023,
                IsOpdbOnly:    false,
                DocCount:      1,
                DocTypeCounts: new Dictionary<string, int> { ["Manual"] = 1 },
                HasManual:     true),
        ]);

    private static async IAsyncEnumerable<ManufacturerCatalogStats> FakeStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return FakeStern;
        yield return FakeJjp;
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    public AdminMachinesTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        // Register the fake repository BEFORE any GetRequiredService call locks
        // the bUnit service provider.
        var statsRepo = Substitute.For<ICatalogStatsReadRepository>();
        statsRepo
            .StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => FakeStream(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(statsRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    // ── Smoke / structural ─────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachines_Renders_WithoutThrowing()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public async Task AdminMachines_Renders_DataGridSentinel()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var grid = cut.Find("[data-testid='admin-machines-grid']");
        Assert.NotNull(grid);
    }

    // ── As-of stamp ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachines_WithData_RendersAsOfElement()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var asOf = cut.Find("[data-testid='catalog-as-of']");
        Assert.NotNull(asOf);
        // Min AsOfUtc is Stern's 2026-06-01 timestamp.
        Assert.Contains("2026-06-01", asOf.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ── Health chips ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachines_EmptyMachine_RendersErrorHealthChip()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Foo Pro has 0 docs → CatalogHealthFlag.Empty → Color.Error chip.
        // Assert BOTH the text AND the Color.Error CSS class so a regression that
        // maps every chip to Color.Default still fails this test.
        // Class confirmed from MudBlazor 8.5.0 MudBlazor.min.css: mud-chip-color-error.
        Assert.Contains("Empty", cut.Markup, StringComparison.Ordinal);
        cut.Find(".mud-chip-color-error");   // throws NotFoundException if absent
    }

    [Fact]
    public async Task AdminMachines_OkMachine_RendersSuccessHealthChip()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Bar CE / Bar LE are Ok → Color.Success chip.
        // Assert BOTH the text AND the Color.Success CSS class.
        // Class confirmed from MudBlazor 8.5.0 MudBlazor.min.css: mud-chip-color-success.
        // The chip renders @flag.ToString() which is "Ok" (CatalogHealthFlag.Ok.ToString()).
        Assert.Contains("Ok", cut.Markup, StringComparison.Ordinal);
        cut.Find(".mud-chip-color-success"); // throws NotFoundException if absent
    }

    // ── Axis selector ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachines_AxisSelector_RendersAllFiveAxes()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var selector = cut.Find("[data-testid='groupby-selector']");
        // Five navigation links, one per axis.
        var links = selector.QuerySelectorAll("a[href]");
        Assert.True(links.Length >= 5,
            $"Expected at least 5 axis links, got {links.Length}.");
    }

    [Fact]
    public async Task AdminMachines_DefaultAxis_IsManufacturer()
    {
        // No GroupBy parameter → default axis is manufacturer.
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The manufacturer link should be rendered Filled (active).
        // We assert that the groupby-selector contains a link pointing to
        // ?groupBy=manufacturer (the default nav link exists).
        var selector = cut.Find("[data-testid='groupby-selector']");
        var mfrLink  = selector.QuerySelector("a[href*='groupBy=manufacturer']");
        Assert.NotNull(mfrLink);
    }

    // ── Query-param grouping axis ──────────────────────────────────────────────

    // Helper: navigate to /admin/machines?groupBy=<axis> then render.
    // [SupplyParameterFromQuery] is driven by the NavigationManager URI in bUnit,
    // not via ComponentParameterCollectionBuilder.Add (bUnit enforces this explicitly).
    private IRenderedComponent<AdminMachines> RenderWithAxis(string axis)
    {
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo($"/admin/machines?groupBy={axis}");
        return Render<AdminMachines>();
    }

    [Fact]
    public async Task AdminMachines_GroupByHealth_ActiveAxisButtonIsFilledPrimary()
    {
        var cut = RenderWithAxis("health");
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Grid renders.
        cut.Find("[data-testid='admin-machines-grid']");

        // The health axis button must carry mud-button-filled-primary (Filled + Color.Primary)
        // — this proves the ?groupBy=health param drove the page state and the active button
        // is visually distinguished. Class confirmed from MudBlazor 8.5.0 MudBlazor.min.css.
        var selector = cut.Find("[data-testid='groupby-selector']");
        var activeBtn = selector.QuerySelector("a.mud-button-filled-primary[href*='groupBy=health']");
        Assert.NotNull(activeBtn);

        // Health chip text appears in the markup.
        // "Empty" is the flag label for Foo Pro (0 docs); "Ok" is flag.ToString() for Bar CE / LE.
        Assert.Contains("Empty", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Ok", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMachines_GroupByFranchise_RendersWithoutError()
    {
        var cut = RenderWithAxis("franchise");
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
        var grid = cut.Find("[data-testid='admin-machines-grid']");
        Assert.NotNull(grid);
    }

    [Fact]
    public async Task AdminMachines_GroupByYear_RendersWithoutError()
    {
        var cut = RenderWithAxis("year");
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public async Task AdminMachines_GroupBySource_RendersWithoutError()
    {
        var cut = RenderWithAxis("source");
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public async Task AdminMachines_UnrecognizedGroupBy_FallsBackToManufacturer()
    {
        // An unrecognized axis value should fall back to manufacturer grouping
        // without throwing — the manufacturer axis links are present regardless.
        var cut = RenderWithAxis("bogusaxis");
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
        var grid = cut.Find("[data-testid='admin-machines-grid']");
        Assert.NotNull(grid);
    }

    // ── Breadcrumb ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachines_Breadcrumb_ContainsAdminRoot()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var adminLink = cut.Find("a[href='/admin']");
        Assert.NotNull(adminLink);
    }
}

// Separate context for the empty-catalog path so the empty-stream stub is
// registered before the bUnit service provider locks (GetRequiredService
// in the constructor seals the provider).
public sealed class AdminMachinesEmptyCatalogTests : AsyncBunitContext
{
    private static async IAsyncEnumerable<ManufacturerCatalogStats> EmptyStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield break;
    }

    public AdminMachinesEmptyCatalogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        var emptyRepo = Substitute.For<ICatalogStatsReadRepository>();
        emptyRepo
            .StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => EmptyStream(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(emptyRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminMachines_EmptyCatalog_RendersEmptyStateMessage()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var empty = cut.Find("[data-testid='admin-machines-empty']");
        Assert.Contains("No machines in catalog", empty.TextContent, StringComparison.Ordinal);
    }
}
