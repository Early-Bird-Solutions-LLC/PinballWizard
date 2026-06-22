# Admin Dashboard counts + Sources grid — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two remaining admin placeholders — the Dashboard's `—` count cards and the empty Sources grid — with real data from existing repositories, on static SSR.

**Architecture:** Both pages stay static SSR (ADR-0034 doctrine — no interactive need) and add `@attribute [StreamRendering]` so the shell renders instantly and bounded reads stream in. The Dashboard derives Machines + Documents counts from one `ICatalogStatsReadRepository` pass, Sources count from `IIngestionSourceRepository`, and Link-Overrides count from `ILinkOverrideRepository`. A new small `AdminCountValue` component renders each count's number / loading / **visible-error** state (Invariant #17 — never a silent dash on failure). The Sources grid streams `IIngestionSourceRepository.StreamAllAsync` into display rows.

**Tech Stack:** Blazor (.NET 10) static SSR + `[StreamRendering]`, MudBlazor 8.x (ADR-0008), bUnit + NSubstitute + xUnit.

## Global Constraints

- **Render mode:** Dashboard + Sources stay **static SSR** — no `@rendermode InteractiveServer`. Add `@attribute [StreamRendering]` only. No `@onclick`/`OnClick=`/`RowClick=`/`@bind-Value`/dialog on either page (`RenderModeConventionTests` must stay green).
- **No new cross-partition Cosmos scan** (ADR-0036): use only `StreamAllManufacturersAsync`, `IIngestionSourceRepository.StreamAllAsync`, `ILinkOverrideRepository.LoadAllAsync`.
- **Visible failure (Invariant #17):** on timeout/error show an explicit error indicator + log; **never** a silent `—` or a fabricated `0`. Use `MudAlert` / `MudIcon` (static-renderable) — **not** `ISnackbar` (requires a circuit; these pages are static).
- **Documents card** counts documents *linked into the catalog* (Σ `MachineDocStats.DocCount`), subtitle "linked into catalog".
- **MudBlazor strict** (ADR-0008): MudBlazor components only; status uses a `MudChip` with a **text label** (colour not the sole meaning carrier); no hardcoded hex colours, `Color.*` tokens only.
- **30s timeout:** each load uses `new CancellationTokenSource(TimeSpan.FromSeconds(30))`, matching the other admin pages.
- **Identity:** commit as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`, conventional `<type>(scope) message`, no Claude attribution trailer.
- **Test command:** `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`.

---

### Task 1: `AdminCountValue` count-state component

A focused, reusable component for the four Dashboard cards: renders the count number, a loading spinner, or a visible error glyph. Used by Task 3. No `@page` (component-only, so `RenderModeConventionTests` does not scan it); uses `MudIcon.Title` (native HTML tooltip) for the error message so it needs **no** popover provider and works on a static page.

**Files:**
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminCountValue.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminCountValueTests.cs`

**Interfaces:**
- Produces: `AdminCountValue` component with parameters `bool Loading`, `bool Failed`, `int? Count`, `string TestId` (EditorRequired). Renders, in priority order: Loading → `<MudText data-testid="{TestId}">` wrapping a spinner; Failed → `<MudIcon ... data-testid="{TestId}-error" Title="Failed to load — see logs">`; else → `<MudText data-testid="{TestId}">{Count}</MudText>`.

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Web.Tests/Components/Admin/AdminCountValueTests.cs`:

```csharp
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminCountValue.razor — the dashboard count-state component.
// Asserts the three mutually-exclusive states (number / loading / visible error)
// so the Invariant #17 failure path (a real error glyph, never a silent dash) is
// behaviourally pinned, not just structurally present.
public sealed class AdminCountValueTests : AsyncBunitContext
{
    public AdminCountValueTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Success_RendersCountNumber()
    {
        var cut = Render<AdminCountValue>(p => p
            .Add(x => x.TestId, "c")
            .Add(x => x.Loading, false)
            .Add(x => x.Failed, false)
            .Add(x => x.Count, 42));

        var el = cut.Find("[data-testid='c']");
        Assert.Equal("42", el.TextContent.Trim());
    }

    [Fact]
    public void Failed_RendersErrorSentinel_NotANumber()
    {
        var cut = Render<AdminCountValue>(p => p
            .Add(x => x.TestId, "c")
            .Add(x => x.Loading, false)
            .Add(x => x.Failed, true)
            .Add(x => x.Count, (int?)null));

        // Visible error glyph present...
        cut.Find("[data-testid='c-error']");
        // ...and the number sentinel is absent (no silent dash / fabricated 0).
        Assert.Empty(cut.FindAll("[data-testid='c']"));
    }

    [Fact]
    public void Loading_RendersCountSentinelWithoutThrowing()
    {
        var cut = Render<AdminCountValue>(p => p
            .Add(x => x.TestId, "c")
            .Add(x => x.Loading, true)
            .Add(x => x.Failed, false)
            .Add(x => x.Count, (int?)null));

        cut.Find("[data-testid='c']");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminCountValueTests"`
Expected: FAIL to compile — `AdminCountValue` does not exist.

- [ ] **Step 3: Write the component**

Create `src/PinballWizard.Web/Components/Pages/Admin/AdminCountValue.razor`:

```razor
@* AdminCountValue — renders a single admin-dashboard summary count.
 *
 * Three mutually-exclusive states: a loading sentinel, a visible error glyph
 * (Invariant #17 — never a silent dash / fabricated 0 on failure), or the
 * number. Uses MudIcon's native Title attribute for the error message so it
 * needs NO popover provider and renders correctly on a static SSR page.
 *
 * Not a @page — RenderModeConventionTests does not scan it; it carries no
 * circuit-dependent control (no OnClick/@bind/dialog).
 *
 * ADR-0008 — MudBlazor strict.
 *@

@if (Loading)
{
    <MudText Typo="Typo.h5" data-testid="@TestId">
        <MudProgressCircular Size="Size.Small" Indeterminate="true" />
    </MudText>
}
else if (Failed)
{
    <MudIcon Icon="@Icons.Material.Filled.ErrorOutline"
             Color="Color.Error"
             Title="Failed to load — see logs"
             data-testid="@($"{TestId}-error")" />
}
else
{
    <MudText Typo="Typo.h5" data-testid="@TestId">@Count</MudText>
}

@code {
    /// <summary>True while the count is loading.</summary>
    [Parameter] public bool Loading { get; set; }

    /// <summary>True if the load failed — renders the visible error glyph.</summary>
    [Parameter] public bool Failed { get; set; }

    /// <summary>The resolved count. Null until loaded or on failure.</summary>
    [Parameter] public int? Count { get; set; }

    /// <summary>data-testid stem; the error glyph uses "{TestId}-error".</summary>
    [Parameter, EditorRequired] public string TestId { get; set; } = default!;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminCountValueTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminCountValue.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminCountValueTests.cs
git commit -m "feat(web) add AdminCountValue count-state component for admin dashboard" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 2: Wire `AdminSources` to `IIngestionSourceRepository`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor` (full rewrite of `@code` + grid)
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminSourcesTests.cs`

**Interfaces:**
- Consumes: `IIngestionSourceRepository.StreamAllAsync(CancellationToken)` → `IAsyncEnumerable<IngestionSource>`. `IngestionSource` fields used: `DisplayName`, `BaseUrl`, `Enabled` (bool), `Cadence`, `LastRunAt` (`DateTimeOffset?`), `LastSuccessAt` (`DateTimeOffset?`), `TotalDocumentsDiscovered` (long), `TotalRunFailures` (long).

- [ ] **Step 1: Write the failing tests**

Replace the body of `tests/PinballWizard.Web.Tests/Components/Admin/AdminSourcesTests.cs` with:

```csharp
using System.Runtime.CompilerServices;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminSources.razor (/admin/sources).
//
// AdminSources is static SSR + [StreamRendering] (ADR-0034 doctrine: no
// interactive need). It streams IIngestionSourceRepository.StreamAllAsync in
// OnInitializedAsync; bUnit runs that synchronously, so WaitForAssertion sees
// the final state. Tests assert the real load path: rows render, the empty-state
// still fires on no sources, and a throwing repo surfaces the visible error
// state (Invariant #17), not a silent empty grid.
public sealed class AdminSourcesTests : AsyncBunitContext
{
    private static IngestionSource MakeSource(string id, bool enabled) => new()
    {
        Id = id,
        DisplayName = $"{id} Pinball",
        ScraperImplKey = id,
        BaseUrl = $"https://{id}.example.com",
        Enabled = enabled,
        Cadence = "weekly",
        TotalDocumentsDiscovered = 7,
        TotalRunFailures = 0,
    };

    private static async IAsyncEnumerable<IngestionSource> Stream(
        IEnumerable<IngestionSource> items,
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        foreach (var i in items) yield return i;
    }

    private static async IAsyncEnumerable<IngestionSource> ThrowingStream(
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162 // unreachable — required to make this a valid iterator
        yield break;
#pragma warning restore CS0162
    }

    private void RegisterSources(Func<CancellationToken, IAsyncEnumerable<IngestionSource>> stream)
    {
        var repo = Substitute.For<IIngestionSourceRepository>();
        repo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => stream(callInfo.Arg<CancellationToken>()));
        Services.AddSingleton(repo);
    }

    public AdminSourcesTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
    }

    [Fact]
    public void WithSources_RendersRows()
    {
        RegisterSources(ct => Stream([MakeSource("stern", true), MakeSource("jjp", false)], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("stern Pinball", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("jjp Pinball", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Enabled", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Disabled", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EmptyList_RendersNoSourcesConfiguredMessage()
    {
        RegisterSources(ct => Stream([], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find("[data-testid='admin-sources-empty']");
            Assert.Contains("No sources configured", empty.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void LoadFailure_RendersVisibleErrorState()
    {
        RegisterSources(ThrowingStream);
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='admin-sources-load-failed']");
            // Failure must NOT masquerade as the benign empty-state.
            Assert.Empty(cut.FindAll("[data-testid='admin-sources-empty']"));
        });
    }

    [Fact]
    public void Breadcrumb_ContainsAdminRoot()
    {
        RegisterSources(ct => Stream([], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() => cut.Find("a[href='/admin']"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminSourcesTests"`
Expected: FAIL — `AdminSources` has no `IIngestionSourceRepository` injection yet, so registration is unused and `admin-sources-load-failed` / row content do not render.

- [ ] **Step 3: Rewrite `AdminSources.razor`**

Replace the entire contents of `src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor` with:

```razor
@page "/admin/sources"
@layout AdminLayout
@attribute [StreamRendering]
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Application.Persistence
@attribute [Authorize(Policy = "AdminOnly")]
@inject IIngestionSourceRepository SourceRepo
@inject ILogger<AdminSources> Logger

@* AdminSources — /admin/sources ingestion-sources list.
 *
 * Static SSR + [StreamRendering] (ADR-0034 doctrine — read-only display, no
 * interactive need): the shell streams immediately and the source rows stream
 * in when the single-partition StreamAllAsync read completes. No SignalR
 * circuit. Failure degrades visibly via MudAlert (Invariant #17), not a silent
 * empty grid — ISnackbar is deliberately NOT used (it needs a circuit).
 *
 * Category: admin surface — uses AdminLayout. MudDataGrid per ADR-0008.
 * Auth: AdminOnly policy (per-page [Authorize], see AdminDashboard.razor).
 *
 * ADR-0008  — MudBlazor strict
 * ADR-0009  — Entra External ID auth
 * ADR-0026  § 1 — routing inventory (/admin/sources)
 * ADR-0034  — render-mode doctrine (static default)
 * ADR-0036  — Cosmos read-access (single-partition stream, no cross-partition scan)
 *@

<PageTitle>Ingestion Sources — PinballWizard Admin</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="py-6">
    <MudBreadcrumbs Items="_breadcrumbs" Class="pa-0 mb-4" />

    <MudText Typo="Typo.h4" GutterBottom="true">Ingestion Sources</MudText>
    <MudText Typo="Typo.body2" Color="Color.Secondary" Class="mb-6">
        Configured scraping sources and their ingestion status.
    </MudText>

    @if (_loading)
    {
        <AdminLoadingBar Label="Loading ingestion sources" />
    }

    @if (_loadFailed)
    {
        <MudAlert Severity="Severity.Error" Class="mb-4" data-testid="admin-sources-load-failed">
            Ingestion sources could not be loaded. Please refresh the page or check Cosmos
            connectivity.
        </MudAlert>
    }

    <MudDataGrid T="IngestionSourceRow"
                 Items="@_sources"
                 Hover="true"
                 Striped="true"
                 Dense="true"
                 Elevation="2"
                 data-testid="admin-sources-grid">

        <Columns>
            <PropertyColumn Property="x => x.Name" Title="Name" />
            <PropertyColumn Property="x => x.SourceUrl" Title="Source URL" />
            <TemplateColumn Title="Status">
                <CellTemplate>
                    <MudChip T="string"
                             Size="Size.Small"
                             Color="@(context.Item.Enabled ? Color.Success : Color.Default)">
                        @(context.Item.Enabled ? "Enabled" : "Disabled")
                    </MudChip>
                </CellTemplate>
            </TemplateColumn>
            <PropertyColumn Property="x => x.Cadence" Title="Cadence" />
            <PropertyColumn Property="x => x.LastRun" Title="Last Run" />
            <PropertyColumn Property="x => x.LastSuccess" Title="Last Success" />
            <PropertyColumn Property="x => x.DocsDiscovered" Title="Docs Discovered" />
            <PropertyColumn Property="x => x.RunFailures" Title="Run Failures" />
        </Columns>

        <NoRecordsContent>
            @* Only the genuine empty result shows this — on a load failure the
             * MudAlert above is the signal, and this is suppressed so a failure
             * never masquerades as "no sources configured". *@
            @if (!_loadFailed)
            {
                <MudStack AlignItems="AlignItems.Center" Class="py-8" Spacing="2">
                    <MudIcon Icon="@Icons.Material.Outlined.Inbox"
                             Size="Size.Large"
                             Color="Color.Tertiary" />
                    <MudText Typo="Typo.body1" data-testid="admin-sources-empty">
                        No sources configured
                    </MudText>
                    <MudText Typo="Typo.body2" Color="Color.Secondary">
                        Ingestion sources are seeded via <code>--seed-ingestion-sources</code>.
                    </MudText>
                </MudStack>
            }
        </NoRecordsContent>

    </MudDataGrid>
</MudContainer>

@code {
    // Display projection of IngestionSource. A null LastRun/LastSuccess renders
    // as "—" — this is legitimate "never run" data, distinct from the load-
    // failure MudAlert above.
    private sealed record IngestionSourceRow(
        string Name,
        string SourceUrl,
        bool Enabled,
        string Cadence,
        string LastRun,
        string LastSuccess,
        long DocsDiscovered,
        long RunFailures);

    private List<IngestionSourceRow> _sources = [];
    private bool _loading = true;
    private bool _loadFailed;

    private readonly List<BreadcrumbItem> _breadcrumbs =
    [
        new BreadcrumbItem("Admin", href: "/admin", icon: Icons.Material.Filled.Dashboard),
        new BreadcrumbItem("Sources", href: "/admin/sources", icon: Icons.Material.Filled.Source),
    ];

    // Static SSR: OnInitializedAsync runs once (no prerender/circuit double-run).
    // [StreamRendering] streams the shell (with the loading bar) first, then the
    // populated grid when the read completes.
    protected override async Task OnInitializedAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var rows = new List<IngestionSourceRow>();
            await foreach (var s in SourceRepo.StreamAllAsync(cts.Token))
            {
                rows.Add(new IngestionSourceRow(
                    Name:           s.DisplayName,
                    SourceUrl:      s.BaseUrl,
                    Enabled:        s.Enabled,
                    Cadence:        s.Cadence,
                    LastRun:        s.LastRunAt?.ToString("u") ?? "—",
                    LastSuccess:    s.LastSuccessAt?.ToString("u") ?? "—",
                    DocsDiscovered: s.TotalDocumentsDiscovered,
                    RunFailures:    s.TotalRunFailures));
            }
            _sources = rows;
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Admin ingestion-sources load timed out after 30 s.");
            _loadFailed = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load admin ingestion sources.");
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminSourcesTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminSourcesTests.cs
git commit -m "feat(web) wire admin sources grid to IIngestionSourceRepository" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 3: Wire `AdminDashboard` counts

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminDashboardTests.cs`

**Interfaces:**
- Consumes: `AdminCountValue` (Task 1); `ICatalogStatsReadRepository.StreamAllManufacturersAsync(CancellationToken)` → `IAsyncEnumerable<ManufacturerCatalogStats>` where `ManufacturerCatalogStats.Machines` is `IReadOnlyList<MachineDocStats>` and `MachineDocStats.DocCount` is `int`; `IIngestionSourceRepository.StreamAllAsync(CancellationToken)`; `ILinkOverrideRepository.LoadAllAsync(CancellationToken)` → `Task<IReadOnlyDictionary<string, LinkOverrideRecord>>`.

- [ ] **Step 1: Write the failing tests**

Replace the body of `tests/PinballWizard.Web.Tests/Components/Admin/AdminDashboardTests.cs` with:

```csharp
using System.Runtime.CompilerServices;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminDashboard.razor (/admin).
//
// Static SSR + [StreamRendering] (ADR-0034). The four cards load from three
// repositories in OnInitializedAsync. Tests assert the real counts render
// (Machines/Documents from catalog_stats, Sources, Link Overrides) and that a
// throwing repo surfaces the per-card error sentinel (Invariant #17) rather
// than a silent dash.
public sealed class AdminDashboardTests : AsyncBunitContext
{
    private static readonly DateTimeOffset AsOf =
        new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // stern: 1 machine / 0 docs ; jjp: 2 machines / (2 + 1) = 3 docs.
    // Totals: 3 machines, 3 documents.
    private static readonly ManufacturerCatalogStats Stern = new(
        "stern", AsOf,
        [new MachineDocStats("mch_a", "Foo", "Pro", "foo", 2024, false, 0,
            new Dictionary<string, int>(), false)]);

    private static readonly ManufacturerCatalogStats Jjp = new(
        "jjp", AsOf,
        [
            new MachineDocStats("mch_b", "Bar CE", "CE", "bar", 2023, false, 2,
                new Dictionary<string, int> { ["Manual"] = 1 }, true),
            new MachineDocStats("mch_c", "Bar LE", "LE", "bar", 2023, false, 1,
                new Dictionary<string, int> { ["Manual"] = 1 }, true),
        ]);

    private static async IAsyncEnumerable<ManufacturerCatalogStats> StatsStream(
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        yield return Stern;
        yield return Jjp;
    }

    private static async IAsyncEnumerable<ManufacturerCatalogStats> ThrowingStatsStream(
        [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<IngestionSource> SourcesStream(
        int count, [EnumeratorCancellation] CancellationToken _)
    {
        await Task.CompletedTask;
        for (var i = 0; i < count; i++)
            yield return new IngestionSource
            {
                Id = $"s{i}", DisplayName = $"Source {i}", ScraperImplKey = $"s{i}",
                BaseUrl = $"https://s{i}.example.com", Enabled = true, Cadence = "weekly",
            };
    }

    private void RegisterAll(
        Func<CancellationToken, IAsyncEnumerable<ManufacturerCatalogStats>> statsStream,
        int sourceCount = 2,
        int overrideCount = 1)
    {
        var stats = Substitute.For<ICatalogStatsReadRepository>();
        stats.StreamAllManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(ci => statsStream(ci.Arg<CancellationToken>()));
        Services.AddSingleton(stats);

        var sources = Substitute.For<IIngestionSourceRepository>();
        sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(ci => SourcesStream(sourceCount, ci.Arg<CancellationToken>()));
        Services.AddSingleton(sources);

        var overrides = Substitute.For<ILinkOverrideRepository>();
        var dict = new Dictionary<string, LinkOverrideRecord>();
        for (var i = 0; i < overrideCount; i++)
            dict[$"p{i}"] = new LinkOverrideRecord
            {
                SourcePattern = $"p{i}", MachineIds = [], CreatedBy = "test", CreatedAt = AsOf,
            };
        overrides.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, LinkOverrideRecord>)dict);
        Services.AddSingleton(overrides);
    }

    public AdminDashboardTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization().SetAuthorized("test-admin@example.com");
    }

    [Fact]
    public void RendersRealCounts()
    {
        RegisterAll(StatsStream, sourceCount: 2, overrideCount: 1);
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("3", cut.Find("[data-testid='admin-machines-count']").TextContent.Trim());
            Assert.Equal("3", cut.Find("[data-testid='admin-documents-count']").TextContent.Trim());
            Assert.Equal("2", cut.Find("[data-testid='admin-sources-count']").TextContent.Trim());
            Assert.Equal("1", cut.Find("[data-testid='admin-link-overrides-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void StatsLoadFailure_RendersErrorSentinels_NotADash()
    {
        RegisterAll(ThrowingStatsStream, sourceCount: 2, overrideCount: 1);
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Machines + Documents share the catalog_stats load → both error.
            cut.Find("[data-testid='admin-machines-count-error']");
            cut.Find("[data-testid='admin-documents-count-error']");
            Assert.Empty(cut.FindAll("[data-testid='admin-machines-count']"));
            // Independent loads are unaffected.
            Assert.Equal("2", cut.Find("[data-testid='admin-sources-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void ViewCatalogButton_HrefsAdminMachines()
    {
        RegisterAll(StatsStream);
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AdminDashboard>();

        cut.WaitForAssertion(() => cut.Find("a[href='/admin/machines']"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminDashboardTests"`
Expected: FAIL — counts still render `—`; no `admin-*-count-error` sentinels; injected repos unused.

- [ ] **Step 3: Rewrite `AdminDashboard.razor`**

Replace the entire contents of `src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor` with:

```razor
@page "/admin"
@layout AdminLayout
@attribute [StreamRendering]
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Application.Persistence
@attribute [Authorize(Policy = "AdminOnly")]
@inject ICatalogStatsReadRepository Stats
@inject IIngestionSourceRepository Sources
@inject ILinkOverrideRepository Overrides
@inject ILogger<AdminDashboard> Logger

@* AdminDashboard — /admin overview page.
 *
 * Static SSR + [StreamRendering] (ADR-0034 doctrine — link cards + read-only
 * counts, no interactive need): the shell streams immediately, then the four
 * summary counts stream in. No SignalR circuit. All counts come from bounded
 * reads (ADR-0036): Machines + Documents from the catalog_stats projection
 * (one pass), Sources from the single 'config' partition, Link Overrides from
 * the bounded LoadAll. A failed load shows a visible error glyph per card
 * (Invariant #17), never a silent dash or fabricated 0.
 *
 * ADR-0008  — MudBlazor strict
 * ADR-0009  — Entra External ID auth
 * ADR-0026  § 1 — routing inventory (/admin)
 * ADR-0034  — render-mode doctrine (static default)
 * ADR-0036  — Cosmos read-access (bounded reads only)
 *@

<PageTitle>Dashboard — PinballWizard Admin</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="py-6">
    <MudBreadcrumbs Items="_breadcrumbs" Class="pa-0 mb-4" />

    <MudText Typo="Typo.h4" GutterBottom="true">Admin Dashboard</MudText>
    <MudText Typo="Typo.body2" Color="Color.Secondary" Class="mb-6">
        System overview — counts and status for the ingestion pipeline.
    </MudText>

    <MudGrid Spacing="3">
        <MudItem xs="12" sm="4">
            <MudCard Elevation="2" Class="admin-summary-card">
                <MudCardContent>
                    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                        <MudIcon Icon="@Icons.Material.Filled.SportsBaseball"
                                 Color="Color.Primary"
                                 Size="Size.Large" />
                        <MudStack Spacing="0">
                            <AdminCountValue TestId="admin-machines-count"
                                             Loading="_loadingStats"
                                             Failed="_statsFailed"
                                             Count="_machinesCount" />
                            <MudText Typo="Typo.body2" Color="Color.Secondary">Machines</MudText>
                        </MudStack>
                    </MudStack>
                </MudCardContent>
                <MudCardActions>
                    <MudButton Href="/admin/machines"
                               Variant="Variant.Text"
                               Color="Color.Primary"
                               Size="Size.Small">
                        View catalog
                    </MudButton>
                </MudCardActions>
            </MudCard>
        </MudItem>

        <MudItem xs="12" sm="4">
            <MudCard Elevation="2" Class="admin-summary-card">
                <MudCardContent>
                    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                        <MudIcon Icon="@Icons.Material.Filled.Source"
                                 Color="Color.Secondary"
                                 Size="Size.Large" />
                        <MudStack Spacing="0">
                            <AdminCountValue TestId="admin-sources-count"
                                             Loading="_loadingSources"
                                             Failed="_sourcesFailed"
                                             Count="_sourcesCount" />
                            <MudText Typo="Typo.body2" Color="Color.Secondary">Ingestion Sources</MudText>
                        </MudStack>
                    </MudStack>
                </MudCardContent>
                <MudCardActions>
                    <MudButton Href="/admin/sources"
                               Variant="Variant.Text"
                               Color="Color.Secondary"
                               Size="Size.Small">
                        View sources
                    </MudButton>
                </MudCardActions>
            </MudCard>
        </MudItem>

        <MudItem xs="12" sm="4">
            <MudCard Elevation="2" Class="admin-summary-card">
                <MudCardContent>
                    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                        <MudIcon Icon="@Icons.Material.Filled.Article"
                                 Color="Color.Tertiary"
                                 Size="Size.Large" />
                        <MudStack Spacing="0">
                            <AdminCountValue TestId="admin-documents-count"
                                             Loading="_loadingStats"
                                             Failed="_statsFailed"
                                             Count="_documentsCount" />
                            <MudText Typo="Typo.body2" Color="Color.Secondary">Documents</MudText>
                            <MudText Typo="Typo.caption" Color="Color.Tertiary">linked into catalog</MudText>
                        </MudStack>
                    </MudStack>
                </MudCardContent>
                <MudCardActions>
                    <MudButton Href="/admin/document-triage"
                               Variant="Variant.Text"
                               Color="Color.Tertiary"
                               Size="Size.Small">
                        Triage unlinked
                    </MudButton>
                </MudCardActions>
            </MudCard>
        </MudItem>

        <MudItem xs="12" sm="4">
            <MudCard Elevation="2" Class="admin-summary-card">
                <MudCardContent>
                    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                        <MudIcon Icon="@Icons.Material.Filled.LinkOff"
                                 Color="Color.Warning"
                                 Size="Size.Large" />
                        <MudStack Spacing="0">
                            <AdminCountValue TestId="admin-link-overrides-count"
                                             Loading="_loadingOverrides"
                                             Failed="_overridesFailed"
                                             Count="_overridesCount" />
                            <MudText Typo="Typo.body2" Color="Color.Secondary">Link Overrides</MudText>
                        </MudStack>
                    </MudStack>
                </MudCardContent>
                <MudCardActions>
                    <MudButton Href="/admin/link-overrides"
                               Variant="Variant.Text"
                               Color="Color.Warning"
                               Size="Size.Small">
                        View overrides
                    </MudButton>
                </MudCardActions>
            </MudCard>
        </MudItem>
    </MudGrid>
</MudContainer>

@code {
    // Machines + Documents share the catalog_stats load (_loadingStats/_statsFailed).
    private bool _loadingStats = true;
    private bool _statsFailed;
    private int? _machinesCount;
    private int? _documentsCount;

    private bool _loadingSources = true;
    private bool _sourcesFailed;
    private int? _sourcesCount;

    private bool _loadingOverrides = true;
    private bool _overridesFailed;
    private int? _overridesCount;

    private readonly List<BreadcrumbItem> _breadcrumbs =
    [
        new BreadcrumbItem("Admin", href: "/admin", icon: Icons.Material.Filled.Dashboard),
    ];

    // Static SSR: runs once. Loads are sequential and independent — one failing
    // load sets only its own card's error flag, leaving the others' counts intact.
    protected override async Task OnInitializedAsync()
    {
        await LoadStatsAsync();
        await LoadSourcesAsync();
        await LoadOverridesAsync();
    }

    private async Task LoadStatsAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var machines = 0;
            var documents = 0;
            await foreach (var mfr in Stats.StreamAllManufacturersAsync(cts.Token))
            {
                machines += mfr.Machines.Count;
                documents += mfr.Machines.Sum(m => m.DocCount);
            }
            _machinesCount = machines;
            _documentsCount = documents;
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Admin dashboard catalog-stats load timed out after 30 s.");
            _statsFailed = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load admin dashboard catalog stats.");
            _statsFailed = true;
        }
        finally
        {
            _loadingStats = false;
        }
    }

    private async Task LoadSourcesAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var count = 0;
            await foreach (var _ in Sources.StreamAllAsync(cts.Token))
            {
                count++;
            }
            _sourcesCount = count;
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Admin dashboard ingestion-sources load timed out after 30 s.");
            _sourcesFailed = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load admin dashboard ingestion-source count.");
            _sourcesFailed = true;
        }
        finally
        {
            _loadingSources = false;
        }
    }

    private async Task LoadOverridesAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var overrides = await Overrides.LoadAllAsync(cts.Token);
            _overridesCount = overrides.Count;
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Admin dashboard link-overrides load timed out after 30 s.");
            _overridesFailed = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load admin dashboard link-override count.");
            _overridesFailed = true;
        }
        finally
        {
            _loadingOverrides = false;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminDashboardTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminDashboardTests.cs
git commit -m "feat(web) wire admin dashboard summary counts to repositories" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 4: Doc hygiene — resolve the "no data transport yet" rationale

**Files:**
- Modify: `docs/superpowers/specs/2026-06-17-admin-render-modes-design.md`

- [ ] **Step 1: Update the render-mode matrix rationale**

In `docs/superpowers/specs/2026-06-17-admin-render-modes-design.md` §3.2, update the two static-page rows to reflect that data transport now exists and is served via static SSR. Change:

```markdown
| `AdminDashboard` (`/admin`) | **static** | link cards only; zero interactivity |
| `AdminSources` (`/admin/sources`) | **static** | read-only grid, no data transport yet |
```

to:

```markdown
| `AdminDashboard` (`/admin`) | **static** + `[StreamRendering]` | summary counts via static-SSR bounded reads; zero interactivity (see 2026-06-21 design) |
| `AdminSources` (`/admin/sources`) | **static** + `[StreamRendering]` | read-only grid loaded via static-SSR stream; zero interactivity (see 2026-06-21 design) |
```

- [ ] **Step 2: Update the §5 non-goal that is now resolved**

In §5, change:

```markdown
- Not making `AdminDashboard` / `AdminSources` interactive (no interactive need today).
```

to:

```markdown
- Not making `AdminDashboard` / `AdminSources` interactive — they load data via
  static SSR + `[StreamRendering]` (2026-06-21 design), which needs no circuit.
```

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/specs/2026-06-17-admin-render-modes-design.md
git commit -m "docs(admin) render-modes spec: dashboard/sources now load via static SSR" --author="Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>"
```

---

### Task 5: Full-suite verification + pre-push self-audit

**Files:** none (verification only)

- [ ] **Step 1: Run the full Web test project**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj`
Expected: PASS — in particular `RenderModeConventionTests` (no interactive signal added → both pages stay valid as static) and `LayoutProviderRenderModeTests` (unchanged) stay green.

- [ ] **Step 2: Build the solution**

Run: `dotnet build PinballWizard.slnx`
Expected: PASS, no warnings introduced (EditorRequired on `AdminCountValue.TestId` is satisfied at every call site).

- [ ] **Step 3: Pre-push self-audit (BLOCKING)**

Run `/local-review` (qualitative) and `/standards-audit` (mechanical gate). Treat any 🔴 as blocking. Confirm:
- No bare `HttpClient.GetAsync` (N/A — no scraper code touched).
- No provenance fields dropped (N/A — read-only display).
- Invariant #17: both pages degrade visibly on load failure (covered by the failure-path tests).
- ADR-0036: no new cross-partition query site added (only existing bounded reads used → no `CrossPartitionQueryAllowListTests` change needed).

- [ ] **Step 4: Manual smoke (optional but recommended)**

Launch the app (`start-apphost.ps1`), sign in through the admin OTP gate, and visit `/admin` and `/admin/sources`: confirm the cards show real counts and the sources grid lists seeded sources (or the empty-state if none seeded).

---

## Notes for the implementer

- **Why static + `[StreamRendering]` and not `InteractiveServer`:** ADR-0034's doctrine is "least-powerful render mode that meets the need." These pages display data; they have no event handlers, two-way binding, dialogs, or live grids. Adding a circuit would contradict the doctrine and the render-mode matrix. `[StreamRendering]` gives the instant-shell UX without a circuit.
- **Why `MudAlert`/`MudIcon`, not `ISnackbar`:** Snackbar needs an interactive circuit; these pages are static. The visible-failure signal must render in the static HTML, so it is a `MudAlert` (Sources) / `MudIcon` error glyph (Dashboard).
- **Why `WaitForAssertion` in tests:** `OnInitializedAsync` is async; even though the fakes complete synchronously, `WaitForAssertion` is the robust bUnit idiom for asserting post-async-init state.
- **`AdminCountValue` is not a `@page`,** so `RenderModeConventionTests` (which scans only routable pages) does not flag it; it carries no circuit-dependent control regardless.
```
