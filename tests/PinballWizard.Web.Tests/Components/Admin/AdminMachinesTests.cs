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
// chips, manufacturer detail links, breadcrumb trail.
//
// Note: AdminMachines is interactive (@rendermode InteractiveServer, ADR-0034
// amendment 2026-06-17) — the sortable/filterable grid needs a live circuit.
// The catalog is a flat grid (grouping was removed): every row's cells,
// including the health chips and manufacturer links, render unconditionally.
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
        ])
        { ManufacturerDisplayName = "Stern Pinball" };

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
        ])
        { ManufacturerDisplayName = "Jersey Jack Pinball" };

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
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Markup);
    }

    [Fact]
    public async Task AdminMachines_Renders_DataGridSentinel()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var grid = cut.Find("[data-testid='admin-machines-grid']");
        Assert.NotNull(grid);
    }

    [Fact]
    public async Task AdminMachines_Renders_GridSearchBox()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='grid-search-input']");
    }

    // ── As-of stamp ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachines_WithData_RendersAsOfElement()
    {
        var cut = RenderWithPopover<AdminMachines>();
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
        var cut = RenderWithPopover<AdminMachines>();
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
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Bar CE / Bar LE are Ok → Color.Success chip.
        // Assert BOTH the text AND the Color.Success CSS class.
        // Class confirmed from MudBlazor 8.5.0 MudBlazor.min.css: mud-chip-color-success.
        // The chip renders @flag.ToString() which is "Ok" (CatalogHealthFlag.Ok.ToString()).
        Assert.Contains("Ok", cut.Markup, StringComparison.Ordinal);
        cut.Find(".mud-chip-color-success"); // throws NotFoundException if absent
    }

    // ── Breadcrumb ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachines_Breadcrumb_ContainsAdminRoot()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var adminLink = cut.Find("a[href='/admin']");
        Assert.NotNull(adminLink);
    }

    // ── Manufacturer detail link (issue #642) ───────────────────────────────────

    [Fact]
    public async Task AdminMachines_ManufacturerCell_LinksToManufacturerDetailPage()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Flat grid — the Manufacturer column always renders as data cells. Stern's
        // rollup carries a display name → its cell links to the public manufacturer
        // detail page with the display text (not the raw key).
        var link = cut.Find("a[href='/manufacturers/stern']");
        Assert.Contains("Stern Pinball", link.TextContent, StringComparison.Ordinal);
    }
}

// Separate context: a pre-backfill rollup that carries the manufacturer KEY but
// no display name. The manufacturer cell must degrade to plain key text with NO
// detail link (Invariant #17 — never an empty/blank link), until a rebuild
// backfills ManufacturerDisplayName.
public sealed class AdminMachinesNullDisplayNameTests : AsyncBunitContext
{
    private static readonly DateTimeOffset FakeAsOf = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly ManufacturerCatalogStats FakeNoDisplay = new(
        Manufacturer: "williams",
        AsOfUtc: FakeAsOf,
        Machines:
        [
            new MachineDocStats(
                MachineId:     "mch_williams_mm",
                Title:         "Medieval Madness",
                EditionLabel:  null,
                GroupId:       null,
                Year:          1997,
                IsOpdbOnly:    true,
                DocCount:      0,
                DocTypeCounts: new Dictionary<string, int>(),
                HasManual:     false),
        ]);   // ManufacturerDisplayName intentionally unset (null) — pre-backfill state

    private static async IAsyncEnumerable<ManufacturerCatalogStats> Stream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return FakeNoDisplay;
    }

    public AdminMachinesNullDisplayNameTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        var repo = Substitute.For<ICatalogStatsReadRepository>();
        repo.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(ci => Stream(ci.Arg<CancellationToken>()));
        Services.AddSingleton(repo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminMachines_NullDisplayName_DegradesToKeyTextWithNoLink()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // No detail link is rendered when the rollup lacks a display name.
        Assert.Empty(cut.FindAll("a[href='/manufacturers/williams']"));
        // The key still renders as text so the column is never blank.
        Assert.Contains("williams", cut.Markup, StringComparison.Ordinal);
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
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var empty = cut.Find("[data-testid='admin-machines-empty']");
        Assert.Contains("No machines in catalog", empty.TextContent, StringComparison.Ordinal);
    }
}

// Behavioral test: page shell + spinner render BEFORE data arrives; data
// populates AFTER. This is the instant-navigation contract (fix/admin-nav-instant-load).
//
// Pattern: hold the repository call with a TaskCompletionSource so we can assert
// the loading state between render and data arrival. The component's OnAfterRenderAsync
// kicks off LoadAsync() asynchronously — the spinner must be present immediately
// after the first render cycle, before the TCS is released.
public sealed class AdminMachinesLoadingStateTests : AsyncBunitContext
{
    private readonly TaskCompletionSource _dataGate = new();

    private async IAsyncEnumerable<ManufacturerCatalogStats> SlowStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Hold until the test releases the gate — simulates slow Cosmos query.
        await _dataGate.Task.WaitAsync(ct);
        yield break;
    }

    public AdminMachinesLoadingStateTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        var slowRepo = Substitute.For<ICatalogStatsReadRepository>();
        slowRepo
            .StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => SlowStream(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(slowRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminMachines_ShowsSpinner_BeforeDataArrives()
    {
        // Render without awaiting the slow data — spinner must be present immediately.
        var cut = RenderWithPopover<AdminMachines>();

        // The loading indicator is visible before the data gate is released.
        // MudProgressLinear renders as a <div> with the mud-progress-indeterminate class.
        Assert.Contains("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);

        // Release the gate so the test teardown doesn't hang.
        _dataGate.SetResult();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AdminMachines_HidesSpinner_AfterDataArrives()
    {
        var cut = RenderWithPopover<AdminMachines>();

        // Spinner present while gate is held.
        Assert.Contains("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);

        // Release the gate, then drain the renderer dispatcher deterministically:
        // SetResult posts the load continuation (which sets _loading = false and calls
        // StateHasChanged) onto the dispatcher. Two InvokeAsync flushes run that
        // continuation and then the resulting re-render, so the assertion never races
        // thread-pool scheduling. WaitForAssertion's wall-clock poll is what flaked
        // under CI load (see project_bunit_gotchas).
        _dataGate.SetResult();
        await cut.InvokeAsync(() => Task.CompletedTask);
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.DoesNotContain("mud-progress-indeterminate", cut.Markup, StringComparison.Ordinal);
    }
}

// Manufacturer-filter tests: when ?manufacturer=stern is present in the URL,
// only machines for that manufacturer are displayed and the filter indicator is shown.
// Without the filter, all machines from all manufacturers are displayed.
//
// [SupplyParameterFromQuery] resolves via NavigationManager.Uri; the test navigates
// the BunitNavigationManager to the filtered URL before rendering so the component
// picks up the query parameter during initialisation.
public sealed class AdminMachinesManufacturerFilterTests : AsyncBunitContext
{
    private static readonly DateTimeOffset FakeAsOf = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // Stern: 1 machine ("Foo Pro")
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
        ])
    { ManufacturerDisplayName = "Stern Pinball" };

    // JJP: 2 machines ("Bar CE", "Bar LE")
    private static readonly ManufacturerCatalogStats FakeJjp = new(
        Manufacturer: "jjp",
        AsOfUtc: FakeAsOf.AddHours(1),
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
        ])
    { ManufacturerDisplayName = "Jersey Jack Pinball" };

    private static async IAsyncEnumerable<ManufacturerCatalogStats> FakeStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return FakeStern;
        yield return FakeJjp;
    }

    public AdminMachinesManufacturerFilterTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        var statsRepo = Substitute.For<ICatalogStatsReadRepository>();
        statsRepo
            .StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(ci => FakeStream(ci.Arg<CancellationToken>()));
        Services.AddSingleton(statsRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task ManufacturerFilter_Stern_ShowsOnlySternMachines()
    {
        // Navigate to the filtered URL so [SupplyParameterFromQuery] resolves "stern"
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo("/admin/machines?manufacturer=stern");

        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Stern's "Foo Pro" must be visible; JJP machines must be hidden
        Assert.Contains("Foo Pro", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Bar CE", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Bar LE", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManufacturerFilter_Stern_ShowsFilterIndicator()
    {
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo("/admin/machines?manufacturer=stern");

        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Filter indicator must render with the manufacturer's display name
        var indicator = cut.Find("[data-testid='machines-manufacturer-filter']");
        Assert.Contains("Stern Pinball", indicator.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoFilter_ShowsAllManufacturerMachines()
    {
        // No navigation — default URL has no manufacturer query param
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Foo Pro", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Bar CE",  cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Bar LE",  cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoFilter_FilterIndicatorAbsent()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Empty(cut.FindAll("[data-testid='machines-manufacturer-filter']"));
    }
}

// Separate context for the Cosmos load-failure path.
// The repo throws so the page must show the distinct error alert and must NOT
// show the "No machines in catalog" empty-state (which implies data, not failure).
public sealed class AdminMachinesLoadFailureTests : AsyncBunitContext
{
    public AdminMachinesLoadFailureTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        var failRepo = Substitute.For<ICatalogStatsReadRepository>();
        failRepo
            .StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<ManufacturerCatalogStats>>(_ =>
                throw new InvalidOperationException("Cosmos unavailable"));
        Services.AddSingleton(failRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public async Task AdminMachines_LoadFails_RendersErrorAlert()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Must render the distinct load-failed sentinel.
        cut.Find("[data-testid='catalog-load-failed']");
    }

    [Fact]
    public async Task AdminMachines_LoadFails_DoesNotRenderEmptyStateText()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The misleading "No machines in catalog" text must be absent — a load failure
        // is not an empty catalog and must not tell admins to re-scrape.
        Assert.Empty(cut.FindAll("[data-testid='admin-machines-empty']"));
    }
}
