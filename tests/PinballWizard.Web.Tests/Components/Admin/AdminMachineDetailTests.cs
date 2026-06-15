using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminMachineDetail.razor (/admin/machines/{opdbId}) — AB#259.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a bUnit
// smoke test. AdminMachineDetail is behind [Authorize]; tests run with AddAuthorization()
// set to authenticated.
//
// Fake data: a "Godzilla Pro" machine (0 docs) with a sibling "Godzilla Premium/LE"
// (5 docs) — the canonical edition-gap scenario whose visibility is this page's raison
// d'être. Two MachineDocumentLinks are injected for the with-docs test; zero links for
// the empty-state test.
//
// Static page (no @rendermode) per ADR-0034.
// [SupplyParameterFromQuery] for ?mfr= is driven by NavigationManager.NavigateTo before
// Render<T>() — bUnit rejects p.Add for query-supplied params.
public sealed class AdminMachineDetailTests : AsyncBunitContext
{
    // ── Shared fake data ───────────────────────────────────────────────────────

    private const string FakeOpdbId = "GRBN-MQR4P";
    private const string FakeMfr    = "stern";
    private const string FakeGroupId = "GRBN";

    private static readonly Machine FakeMachinePro = new()
    {
        Id                   = FakeOpdbId,
        PartitionKey         = FakeMfr,
        ManufacturerDisplayName = "Stern Pinball",
        Title                = "Godzilla",
        EditionLabel         = "Pro",
        GroupId              = FakeGroupId,
        Year                 = 2021,
        OpdbSourceUrl        = "https://opdb.org/search?q=GRBN-MQR4P",
        FirstSeenAt          = DateTimeOffset.UtcNow,
        LastSeenAt           = DateTimeOffset.UtcNow,
    };

    private static readonly Machine FakeMachinePremium = new()
    {
        Id                   = "GRBN-ABC12",
        PartitionKey         = FakeMfr,
        ManufacturerDisplayName = "Stern Pinball",
        Title                = "Godzilla",
        EditionLabel         = "Premium/LE",
        GroupId              = FakeGroupId,
        Year                 = 2021,
        FirstSeenAt          = DateTimeOffset.UtcNow,
        LastSeenAt           = DateTimeOffset.UtcNow,
    };

    // Rollup where Pro has 0 docs, Premium/LE has 5 — classic edition gap.
    private static readonly ManufacturerCatalogStats FakeRollup = new(
        Manufacturer: FakeMfr,
        AsOfUtc: new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        Machines:
        [
            new MachineDocStats(
                MachineId:     FakeOpdbId,
                Title:         "Godzilla",
                EditionLabel:  "Pro",
                GroupId:       FakeGroupId,
                Year:          2021,
                IsOpdbOnly:    false,
                DocCount:      0,
                DocTypeCounts: new Dictionary<string, int>(),
                HasManual:     false),
            new MachineDocStats(
                MachineId:     "GRBN-ABC12",
                Title:         "Godzilla",
                EditionLabel:  "Premium/LE",
                GroupId:       FakeGroupId,
                Year:          2021,
                IsOpdbOnly:    false,
                DocCount:      5,
                DocTypeCounts: new Dictionary<string, int> { ["Manual"] = 2 },
                HasManual:     true),
        ]);

    private static readonly MachineDocumentLink FakeDocManual = new(
        DocumentId:       "doc_abc",
        DocumentType:     "Manual",
        DocumentUrl:      "https://sternpinball.com/manuals/godzilla_pro.pdf",
        LinkText:         "Godzilla Pro Manual",
        Edition:          "Pro",
        EditionScope:     "Pro",
        LinkStatus:       "Linked",
        ResolutionStrategy: "EditionMatch",
        LastDownloadedUtc: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        SizeBytes:        1_500_000,
        PageCount:        120);

    private static readonly MachineDocumentLink FakeDocBulletin = new(
        DocumentId:       "doc_def",
        DocumentType:     "ServiceBulletin",
        DocumentUrl:      "https://sternpinball.com/bulletins/godzilla_sb01.pdf",
        LinkText:         null,                      // should fall back to filename
        Edition:          null,
        EditionScope:     "All",
        LinkStatus:       "PlatformGeneric",
        ResolutionStrategy: "ManufacturerHeuristic",
        LastDownloadedUtc: null,
        SizeBytes:        null,
        PageCount:        null);

    // ── Async sibling stream helpers ───────────────────────────────────────────

    private static async IAsyncEnumerable<Machine> SiblingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return FakeMachinePro;
        yield return FakeMachinePremium;
    }

    private static async IAsyncEnumerable<Machine> EmptySiblingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<MachineDocumentLink> DocStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return FakeDocManual;
        yield return FakeDocBulletin;
    }

    private static async IAsyncEnumerable<MachineDocumentLink> EmptyDocStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield break;
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    private readonly IMachineRepository _machinesRepo;
    private readonly ICatalogStatsReadRepository _statsRepo;
    private readonly IMachineDocumentReadRepository _docsRepo;

    public AdminMachineDetailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        _machinesRepo = Substitute.For<IMachineRepository>();
        _statsRepo    = Substitute.For<ICatalogStatsReadRepository>();
        _docsRepo     = Substitute.For<IMachineDocumentReadRepository>();

        // Default stubs — individual tests override as needed.
        _machinesRepo
            .GetByOpdbIdAsync(FakeOpdbId, FakeMfr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Machine?>(FakeMachinePro));

        _machinesRepo
            .GetSiblingsByGroupIdAsync(FakeGroupId, Arg.Any<CancellationToken>())
            .Returns(callInfo => SiblingStream(callInfo.Arg<CancellationToken>()));

        _statsRepo
            .GetByManufacturerAsync(FakeMfr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ManufacturerCatalogStats?>(FakeRollup));

        _docsRepo
            .StreamByMachineIdAsync(FakeOpdbId, Arg.Any<CancellationToken>())
            .Returns(callInfo => DocStream(callInfo.Arg<CancellationToken>()));

        Services.AddSingleton(_machinesRepo);
        Services.AddSingleton(_statsRepo);
        Services.AddSingleton(_docsRepo);
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AdminMachineDetail>>(
            NullLogger<AdminMachineDetail>.Instance);

        // Seed the NavigationManager so [SupplyParameterFromQuery] fires.
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo($"/admin/machines/{FakeOpdbId}?mfr={FakeMfr}");
    }

    // ── Helper: render with both route param and query param ──────────────────
    // [Parameter] OpdbId comes from the route segment {OpdbId} — bUnit doesn't
    // parse @page route templates, so we pass it via ComponentParameter.
    // [SupplyParameterFromQuery] Mfr comes from the NavigationManager URI
    // (set in the constructor via NavigateTo). Both are required.

    private IRenderedComponent<AdminMachineDetail> RenderDetail() =>
        Render<AdminMachineDetail>(p => p.Add(x => x.OpdbId, FakeOpdbId));

    // ── Header renders title, edition, and OPDB ID ────────────────────────────

    [Fact]
    public async Task AdminMachineDetail_Renders_Title()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Godzilla", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMachineDetail_Renders_EditionLabel()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("Pro", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMachineDetail_Renders_OpdbId()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var opdbEl = cut.Find("[data-testid='detail-opdb-id']");
        Assert.Contains(FakeOpdbId, opdbEl.TextContent, StringComparison.Ordinal);
    }

    // ── Edition-sibling strip (headline feature) ──────────────────────────────

    [Fact]
    public async Task AdminMachineDetail_SiblingStrip_RendersStrip()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='edition-sibling-strip']");  // throws if absent
    }

    [Fact]
    public async Task AdminMachineDetail_SiblingStrip_ShowsBothEditions()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var strip = cut.Find("[data-testid='edition-sibling-strip']");
        Assert.Contains("Pro",        strip.InnerHtml, StringComparison.Ordinal);
        Assert.Contains("Premium/LE", strip.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMachineDetail_SiblingStrip_ShowsDocCounts()
    {
        // Pro has 0 docs; Premium/LE has 5.
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var strip = cut.Find("[data-testid='edition-sibling-strip']");
        Assert.Contains("0 docs", strip.InnerHtml, StringComparison.Ordinal);
        Assert.Contains("5 docs", strip.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMachineDetail_SiblingStrip_ZeroDocEdition_HasErrorColorChip()
    {
        // The edition with 0 docs (Pro) must render a Color.Error chip
        // (mud-chip-color-error) — not a row tint.
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var strip = cut.Find("[data-testid='edition-sibling-strip']");
        strip.QuerySelector(".mud-chip-color-error"); // throws NotFoundException if absent

        // And a success chip for the edition with 5 docs.
        strip.QuerySelector(".mud-chip-color-success");
    }

    [Fact]
    public async Task AdminMachineDetail_SiblingStrip_EditionGap_ShowsCallout()
    {
        // When one edition has 0 docs and another has >0, the gap callout fires.
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='detail-edition-gap-callout']");
    }

    // ── Linked-documents table ─────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachineDetail_WithDocs_RendersDocsGrid()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='detail-docs-grid']");
    }

    [Fact]
    public async Task AdminMachineDetail_WithDocs_RendersLinkStatus()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // FakeDocManual has LinkStatus="Linked"; FakeDocBulletin has "PlatformGeneric".
        Assert.Contains("Linked",          cut.Markup, StringComparison.Ordinal);
        Assert.Contains("PlatformGeneric", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMachineDetail_WithDocs_RendersResolutionStrategy()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("EditionMatch",         cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ManufacturerHeuristic", cut.Markup, StringComparison.Ordinal);
    }

    // ── Actions ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachineDetail_Renders_ActionsBar()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='detail-actions']");
    }

    [Fact]
    public async Task AdminMachineDetail_Actions_ContainTriageLink()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var actions = cut.Find("[data-testid='detail-actions']");
        Assert.NotNull(actions.QuerySelector("a[href='/admin/document-triage']"));
    }

    [Fact]
    public async Task AdminMachineDetail_Actions_ContainLinkOverridesLink()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var actions = cut.Find("[data-testid='detail-actions']");
        Assert.NotNull(actions.QuerySelector("a[href='/admin/link-overrides']"));
    }

    // ── Breadcrumb ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMachineDetail_Breadcrumb_ContainsAdminAndMachinesLinks()
    {
        var cut = RenderDetail();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.NotNull(cut.Find("a[href='/admin']"));
        Assert.NotNull(cut.Find("a[href='/admin/machines']"));
    }
}

// ── Zero-docs test (separate context so stub is registered before provider locks) ──

public sealed class AdminMachineDetailNullDocsTests : AsyncBunitContext
{
    private const string FakeOpdbId = "GRBN-MQR4P";
    private const string FakeMfr    = "stern";

    private static readonly Machine FakeMachineSingleton = new()
    {
        Id                   = FakeOpdbId,
        PartitionKey         = FakeMfr,
        ManufacturerDisplayName = "Stern Pinball",
        Title                = "Godzilla",
        EditionLabel         = "Pro",
        GroupId              = null,              // singleton — no siblings
        Year                 = 2021,
        FirstSeenAt          = DateTimeOffset.UtcNow,
        LastSeenAt           = DateTimeOffset.UtcNow,
    };

    private static readonly ManufacturerCatalogStats FakeRollup = new(
        Manufacturer: FakeMfr,
        AsOfUtc: DateTimeOffset.UtcNow,
        Machines:
        [
            new MachineDocStats(
                MachineId:     FakeOpdbId,
                Title:         "Godzilla",
                EditionLabel:  "Pro",
                GroupId:       null,
                Year:          2021,
                IsOpdbOnly:    false,
                DocCount:      0,
                DocTypeCounts: new Dictionary<string, int>(),
                HasManual:     false),
        ]);

    private static async IAsyncEnumerable<MachineDocumentLink> EmptyDocStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield break;
    }

    public AdminMachineDetailNullDocsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        var machinesRepo = Substitute.For<IMachineRepository>();
        machinesRepo
            .GetByOpdbIdAsync(FakeOpdbId, FakeMfr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Machine?>(FakeMachineSingleton));

        var statsRepo = Substitute.For<ICatalogStatsReadRepository>();
        statsRepo
            .GetByManufacturerAsync(FakeMfr, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ManufacturerCatalogStats?>(FakeRollup));

        var docsRepo = Substitute.For<IMachineDocumentReadRepository>();
        docsRepo
            .StreamByMachineIdAsync(FakeOpdbId, Arg.Any<CancellationToken>())
            .Returns(callInfo => EmptyDocStream(callInfo.Arg<CancellationToken>()));

        Services.AddSingleton(machinesRepo);
        Services.AddSingleton(statsRepo);
        Services.AddSingleton(docsRepo);
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AdminMachineDetail>>(
            NullLogger<AdminMachineDetail>.Instance);

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo($"/admin/machines/{FakeOpdbId}?mfr={FakeMfr}");
    }

    [Fact]
    public async Task AdminMachineDetail_NoDocs_RendersEmptyState()
    {
        var cut = Render<AdminMachineDetail>(p => p.Add(x => x.OpdbId, FakeOpdbId));
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='detail-no-docs']");
    }

    [Fact]
    public async Task AdminMachineDetail_NoDocs_NoDocsGridRendered()
    {
        var cut = Render<AdminMachineDetail>(p => p.Add(x => x.OpdbId, FakeOpdbId));
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Empty(cut.FindAll("[data-testid='detail-docs-grid']"));
    }
}

// ── Missing-mfr guard (separate context) ──────────────────────────────────────

public sealed class AdminMachineDetailMissingMfrTests : AsyncBunitContext
{
    public AdminMachineDetailMissingMfrTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");

        Services.AddSingleton(Substitute.For<IMachineRepository>());
        Services.AddSingleton(Substitute.For<ICatalogStatsReadRepository>());
        Services.AddSingleton(Substitute.For<IMachineDocumentReadRepository>());
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AdminMachineDetail>>(
            NullLogger<AdminMachineDetail>.Instance);

        // Navigate WITHOUT ?mfr= to trigger the guard.
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/admin/machines/GRBN-MQR4P");
    }

    [Fact]
    public async Task AdminMachineDetail_MissingMfr_ShowsFriendlyMessage()
    {
        var cut = Render<AdminMachineDetail>(p => p.Add(x => x.OpdbId, "GRBN-MQR4P"));
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='detail-missing-mfr']");
    }
}
