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

// bUnit tests for AdminMachines.razor (/admin/machines) — rewritten for AB#259.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AdminMachines is behind [Authorize]; tests run with
// AddAuthorization() set to authenticated.
//
// ICatalogStatsReadRepository is faked per test via NSubstitute returning two
// manufacturers — one machine with 0 docs (Empty flag), one all-OK machine.
// Tests assert behavioral invariants: grid sentinel renders, "as of" timestamp
// is present, health chip for the Empty machine appears, and breadcrumb links
// back to the admin root.
//
// Note: AdminMachines has no @rendermode directive (ADR-0034 — static page).
// bUnit renders it synchronously via OnInitializedAsync; tests await the
// async initializer to let the fake repository resolve.
public sealed class AdminMachinesTests : AsyncBunitContext
{
    // ── Fake data ──────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset _fakeAsOf =
        new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // Two manufacturers, three machines total:
    //   Stern   → "Foo Pro"  (0 docs → Empty flag)
    //   JJP     → "Bar CE"   (2 docs, HasManual → Ok)
    //             "Bar LE"   (1 doc, HasManual → Ok)
    private static readonly ManufacturerCatalogStats FakeStern = new(
        Manufacturer: "stern",
        AsOfUtc: _fakeAsOf,
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
        AsOfUtc: _fakeAsOf.AddHours(1),   // later → min is Stern's timestamp
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

    // IAsyncEnumerable returning the two fake manufacturers.
    private static async IAsyncEnumerable<ManufacturerCatalogStats> FakeStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return FakeStern;
        yield return FakeJjp;
    }

    // Empty stream — simulates an unpopulated catalog.
    private static async IAsyncEnumerable<ManufacturerCatalogStats> EmptyStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield break;
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    public AdminMachinesTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        // Register the fake repository BEFORE GetRequiredService calls lock
        // the bUnit service provider.
        var statsRepo = Substitute.For<ICatalogStatsReadRepository>();
        statsRepo
            .StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => FakeStream(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(statsRepo);

        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

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

    [Fact]
    public async Task AdminMachines_WithData_RendersAsOfElement()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The "as of" honesty stamp must be present when data loads.
        var asOf = cut.Find("[data-testid='catalog-as-of']");
        Assert.NotNull(asOf);
        // Content references the minimum AsOfUtc across manufacturers (Stern's).
        Assert.Contains("2026-06-01", asOf.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminMachines_EmptyMachine_RendersErrorHealthChip()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The Foo Pro machine has 0 docs → CatalogHealthFlag.Empty → Color.Error chip.
        // MudChip renders with a mud-chip element; assert the text "Empty" appears
        // in the rendered markup (behavior, not specific chip CSS class).
        Assert.Contains("Empty", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMachines_WithData_RendersOkHealthChip()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Bar CE and Bar LE are both Ok (HasManual=true, DocCount>0).
        Assert.Contains("Ok", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMachines_Breadcrumb_ContainsAdminRoot()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var adminLink = cut.Find("a[href='/admin']");
        Assert.NotNull(adminLink);
    }
}

// Separate context for the empty-catalog path so the empty-stream
// stub can be registered before the bUnit service provider locks.
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
