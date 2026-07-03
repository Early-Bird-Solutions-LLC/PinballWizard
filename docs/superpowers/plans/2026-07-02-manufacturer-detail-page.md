# Manufacturer Detail Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a public `/manufacturers/{key}` detail page (all games + grouped document counts), add the missing **Manufacturers** admin-nav tab, and link every manufacturer mention in the focused admin+docs surfaces to the new page.

**Architecture:** A new public Blazor page reads the authoritative games list from `IMachineRepository.StreamByManufacturerAsync` (single-partition, works for every manufacturer) and left-joins per-machine document counts from the `ICatalogStatsReadRepository.GetByManufacturerAsync` rollup (point read; absent ⇒ zero, honest). Documents are shown as grouped counts with a link out to the already-leak-safe public `/documents?manufacturer=` surface — no new by-manufacturer document query, no operational internals exposed. A small `ManufacturerLink` shared component centralizes the `/manufacturers/{key}` URL for all call-sites.

**Tech Stack:** .NET 10, Blazor (InteractiveServer), MudBlazor (strict, via ADR-0046 `App*` wrappers), bUnit + NSubstitute + xUnit.

## Global Constraints

- **Branch:** `feat/manufacturer-detail-page` in worktree `.worktrees/manufacturer-detail-page` (already created off clean `origin/main`). Do all work there. The main tree is on another session's branch — do not touch it.
- **Identity (INVARIANT):** every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. **No Claude attribution trailer.**
- **MudBlazor strict (ADR-0008):** no raw HTML for headings/links/tables. Use the ADR-0046 shared wrappers (`AppDataGrid`, `AppPageHeader`, `AppErrorAlert`, `AppEmptyState`, `AppStatusChip`, `AdminLoadingBar`). `MudTable`/`MudSimpleTable` are banned from the page layer.
- **No hardcoded colors:** `Color.*` enum only.
- **Invariant #17 (fallbacks degrade visibly):** never fabricate content; a failed read shows an alert / "—", never a fake success.
- **Favoritism guardrail:** the games grid sorts alphabetically by title — no ranking.
- **Security:** the public page reads ONLY the machine catalog + `catalog_stats` rollup and links to the public `/documents` filter. It renders NO operational internals (scraper enable/disable, run history, base URLs, politeness, link-failure reasons, raw/untriaged docs).
- **No XML doc comments** on public surface (repo convention). File-top `@* … *@` component comments are fine.
- **Verify before done:** `/local-review` + `/standards-audit`; full CI-equivalent test filter before push.

## File Structure

| File | Responsibility |
| --- | --- |
| `src/PinballWizard.Web/Components/Shared/ManufacturerLink.razor` (create) | Renders a `MudLink` to `/manufacturers/{key}`. One owner of the URL shape. |
| `src/PinballWizard.Web/Components/Pages/Manufacturers.razor` (create) | Public detail page: games grid + grouped doc counts + browse-all link + honest states. |
| `src/PinballWizard.Web/Components/Layout/AdminLayout.razor` (modify) | Add the Manufacturers `NavRailItem`. |
| `src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor` (modify) | Repoint the name column from `/admin/sources/{key}` to `ManufacturerLink`. |
| `src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor` (modify) | Manufacturer field → `ManufacturerLink`. |
| `src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor` (modify) | Manufacturer field → `ManufacturerLink`. **NOTE: also edited by a parallel session — re-check before editing.** |
| `src/PinballWizard.Web/Components/Shared/DocumentList.razor` (modify) | Manufacturer `PropertyColumn` → `TemplateColumn` with `ManufacturerLink`. |
| `src/PinballWizard.Web/Components/Pages/DocumentDetail.razor` (modify) | Manufacturer field → `ManufacturerLink`. |
| `tests/PinballWizard.Web.Tests/Components/Shared/ManufacturerLinkTests.cs` (create) | Link renders correct href + text. |
| `tests/PinballWizard.Web.Tests/Components/Pages/ManufacturersPageTests.cs` (create) | Page behavior: happy, OPDB-only, load-fail, not-found, stats-section-fail. |
| `tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs` (modify) | Assert the Manufacturers nav link renders. |

**Task order & dependencies:** Task 1 (`ManufacturerLink`) → Task 2 (page; defines the route the link targets) → Task 3 (nav tab; independent) → Task 4 (fan-out; depends on Task 1 + Task 2's route).

---

### Task 1: `ManufacturerLink` shared component

**Files:**
- Create: `src/PinballWizard.Web/Components/Shared/ManufacturerLink.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Shared/ManufacturerLinkTests.cs`

**Interfaces:**
- Produces: `<ManufacturerLink ManufacturerKey="stern" DisplayName="Stern Pinball" />` → `<a href="/manufacturers/stern">Stern Pinball</a>`. Optional `Typo` param (default `Typo.body2`).

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Shared;

// ManufacturerLink centralizes the /manufacturers/{key} URL shape used by every
// call-site (ADR-0046 shared-component doctrine). It renders a MudLink whose href
// is the manufacturer detail route and whose text is the display name.
public sealed class ManufacturerLinkTests : TestContext
{
    public ManufacturerLinkTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Renders_LinkToDetailRoute_WithDisplayNameText()
    {
        var cut = RenderComponent<ManufacturerLink>(p => p
            .Add(x => x.ManufacturerKey, "stern")
            .Add(x => x.DisplayName, "Stern Pinball"));

        var anchor = cut.Find("a[href='/manufacturers/stern']");
        Assert.Contains("Stern Pinball", anchor.TextContent, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~ManufacturerLinkTests"`
Expected: FAIL — `ManufacturerLink` does not exist (compile error).

- [ ] **Step 3: Write the component**

Create `src/PinballWizard.Web/Components/Shared/ManufacturerLink.razor`:

```razor
@namespace PinballWizard.Web.Components.Shared

@* ManufacturerLink — the single owner of the /manufacturers/{key} URL shape.
 * Every call-site that shows a manufacturer name links through this component so
 * the route contract lives in one place (ADR-0046 shared-component doctrine). *@

<MudLink Href="@($"/manufacturers/{ManufacturerKey}")" Typo="@Typo" data-testid="manufacturer-link">@DisplayName</MudLink>

@code {
    [Parameter, EditorRequired] public string ManufacturerKey { get; set; } = default!;
    [Parameter, EditorRequired] public string DisplayName { get; set; } = default!;
    [Parameter] public Typo Typo { get; set; } = Typo.body2;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~ManufacturerLinkTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
WT="c:/earlybird/PinballWizard/.worktrees/manufacturer-detail-page"
git -C "$WT" add src/PinballWizard.Web/Components/Shared/ManufacturerLink.razor tests/PinballWizard.Web.Tests/Components/Shared/ManufacturerLinkTests.cs
git -C "$WT" -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(web) ManufacturerLink shared component → /manufacturers/{key}"
```

---

### Task 2: `/manufacturers/{key}` public detail page

**Files:**
- Create: `src/PinballWizard.Web/Components/Pages/Manufacturers.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Pages/ManufacturersPageTests.cs`

**Interfaces:**
- Consumes: `IMachineRepository.StreamByManufacturerAsync(string key, CancellationToken)` → `IAsyncEnumerable<Machine>`; `ICatalogStatsReadRepository.GetByManufacturerAsync(string key, CancellationToken)` → `Task<ManufacturerCatalogStats?>`. Join key: `MachineDocStats.MachineId == Machine.Id`.
- Produces: route `/manufacturers/{Key}`.
- Lifecycle rationale: loads in `OnInitializedAsync` with a local 30 s CTS — mirrors the sibling `AdminManufacturers` page exactly (which accepts the prerender read for a much heavier cross-partition stream; this page's reads are lighter — one single-partition stream + one point read).

- [ ] **Step 1: Write the failing tests**

Create `tests/PinballWizard.Web.Tests/Components/Pages/ManufacturersPageTests.cs`:

```csharp
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Pages;

// bUnit tests for Manufacturers.razor (/manufacturers/{key}) — public catalog page.
// Games list is authoritative from IMachineRepository.StreamByManufacturerAsync
// (single-partition, works for every manufacturer). Per-machine doc counts are a
// left-join from the ICatalogStatsReadRepository rollup (join on MachineDocStats.
// MachineId == Machine.Id). Honest states (Invariant #17): machine-load-fail alert,
// not-found for an unknown key, and a stats-read failure that degrades doc counts to
// "—" while the games list still renders.
public sealed class ManufacturersPageTests : AsyncBunitContext
{
    private readonly IMachineRepository _machines = Substitute.For<IMachineRepository>();
    private readonly ICatalogStatsReadRepository _stats = Substitute.For<ICatalogStatsReadRepository>();

    public ManufacturersPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_machines);
        Services.AddSingleton(_stats);
        Services.AddSingleton<ILogger<Manufacturers>>(NullLogger<Manufacturers>.Instance);
    }

    private static Machine M(string key, string display, string id, string title, int? year, string? edition) => new()
    {
        Id = id, PartitionKey = key, ManufacturerDisplayName = display, Title = title,
        Year = year, EditionLabel = edition,
        FirstSeenAt = DateTimeOffset.MinValue, LastSeenAt = DateTimeOffset.MinValue,
    };

    private static MachineDocStats S(string id, string title, int docCount, bool hasManual) =>
        new(id, title, EditionLabel: null, GroupId: null, Year: null, IsOpdbOnly: false,
            DocCount: docCount, DocTypeCounts: new Dictionary<string, int>(), HasManual: hasManual);

    private static async IAsyncEnumerable<T> Stream<T>(params T[] items)
    {
        await Task.CompletedTask;
        foreach (var i in items) yield return i;
    }

    private static async IAsyncEnumerable<T> Throwing<T>()
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("simulated Cosmos failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private IRenderedComponent<Manufacturers> RenderPage(string key)
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Manufacturers>(1);
            builder.AddAttribute(2, nameof(Manufacturers.Key), key);
            builder.CloseComponent();
        });
        return fragment.FindComponent<Manufacturers>();
    }

    [Fact]
    public async Task Populated_RendersGamesWithDocCountsAndBrowseAllLink()
    {
        _machines.StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(_ => Stream(
                M("stern", "Stern Pinball", "GRBN-MQR4P", "Stranger Things", 2019, "Pro"),
                M("stern", "Stern Pinball", "GXYZ-1", "Godzilla", 2021, null)));
        _stats.GetByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(new ManufacturerCatalogStats("stern", DateTimeOffset.MinValue,
                new[] { S("GRBN-MQR4P", "Stranger Things", 3, true), S("GXYZ-1", "Godzilla", 5, true) }));

        var cut = RenderPage("stern");
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturer-games-table']");
        Assert.Contains("Stranger Things", table.TextContent, StringComparison.Ordinal);
        Assert.Contains("Godzilla", table.TextContent, StringComparison.Ordinal);
        // Grouped-by-machine doc counts present; total (8) surfaced via the browse-all link.
        var browse = cut.Find("[data-testid='manufacturer-browse-docs']");
        Assert.Contains("8", browse.TextContent, StringComparison.Ordinal);
        cut.Find("a[href='/documents?manufacturer=Stern%20Pinball']");
        // Alphabetical order (favoritism guardrail): Godzilla before Stranger Things.
        Assert.True(
            table.TextContent.IndexOf("Godzilla", StringComparison.Ordinal) <
            table.TextContent.IndexOf("Stranger Things", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpdbOnlyManufacturer_NoRollup_ShowsGamesZeroDocsNoBrowseLink()
    {
        // Williams has machines but no catalog_stats rollup (OPDB-only, no scraper).
        _machines.StreamByManufacturerAsync("williams", Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("williams", "Williams", "W-1", "Medieval Madness", 1997, null)));
        _stats.GetByManufacturerAsync("williams", Arg.Any<CancellationToken>())
            .Returns((ManufacturerCatalogStats?)null);

        var cut = RenderPage("williams");
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturer-games-table']");
        Assert.Contains("Medieval Madness", table.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='manufacturer-browse-docs']"));
    }

    [Fact]
    public async Task UnknownKey_NoMachines_RendersNotFound()
    {
        _machines.StreamByManufacturerAsync("nope", Arg.Any<CancellationToken>())
            .Returns(_ => Stream<Machine>());

        var cut = RenderPage("nope");
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturer-not-found']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturer-games-table']"));
    }

    [Fact]
    public async Task MachineStreamFails_RendersVisibleAlertNoTable()
    {
        _machines.StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(_ => Throwing<Machine>());

        var cut = RenderPage("stern");
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='manufacturer-load-failed']");
        Assert.Empty(cut.FindAll("[data-testid='manufacturer-games-table']"));
    }

    [Fact]
    public async Task StatsReadFails_GamesStillRender_DocCountsDegradeToDash()
    {
        // Section-scoped: rollup read throws → games list from read 1 survives, doc
        // counts show "—", no browse-all link (Invariant #17 — no fabricated counts).
        _machines.StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("stern", "Stern Pinball", "GXYZ-1", "Godzilla", 2021, null)));
        _stats.GetByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns<Task<ManufacturerCatalogStats?>>(_ => throw new InvalidOperationException("stats down"));

        var cut = RenderPage("stern");
        await cut.InvokeAsync(() => Task.CompletedTask);

        var table = cut.Find("[data-testid='manufacturer-games-table']");
        Assert.Contains("Godzilla", table.TextContent, StringComparison.Ordinal);
        Assert.Contains("—", table.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='manufacturer-browse-docs']"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~ManufacturersPageTests"`
Expected: FAIL — `Manufacturers` page does not exist (compile error).

- [ ] **Step 3: Write the page**

Create `src/PinballWizard.Web/Components/Pages/Manufacturers.razor`:

```razor
@page "/manufacturers/{Key}"
@using Microsoft.AspNetCore.Authorization
@using PinballWizard.Application.Catalog
@using PinballWizard.Application.Persistence
@using PinballWizard.Core.Domain
@attribute [AllowAnonymous]
@rendermode InteractiveServer

@* Manufacturers — /manufacturers/{key} public catalog-by-brand detail page.
 *
 * Public (MainLayout default, [AllowAnonymous]) so every manufacturer mention in the
 * app can link here without bouncing anonymous users to sign-in. Two reads in
 * OnInitializedAsync (mirrors the sibling AdminManufacturers lifecycle):
 *   1. IMachineRepository.StreamByManufacturerAsync(key) — authoritative games list,
 *      single-partition, works for EVERY manufacturer incl. OPDB-only ones.
 *   2. ICatalogStatsReadRepository.GetByManufacturerAsync(key) — per-manufacturer
 *      rollup point read; left-joined by MachineDocStats.MachineId == Machine.Id for
 *      per-machine doc counts. Null rollup = OPDB-only (zero docs, honest). A throw is
 *      section-scoped: doc counts degrade to "—", the games list still renders (#17).
 *
 * Documents are shown as grouped-by-machine counts + a link out to the already
 * leak-safe public /documents?manufacturer= surface — NO operational internals
 * (scraper status, run history, politeness, raw/untriaged docs) are exposed here.
 *
 * ADR-0008 — MudBlazor strict   ADR-0034 — interactive (sortable grid)
 * ADR-0036 — Cosmos read-access (single-partition stream + point read)
 *@

@inject IMachineRepository Machines
@inject ICatalogStatsReadRepository Stats
@inject ILogger<Manufacturers> Logger

<PageTitle>@(_displayName ?? Key) — PinballWizard</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="mt-6">
    @if (_loading)
    {
        <AdminLoadingBar Label="Loading manufacturer" />
    }
    else if (_loadFailed)
    {
        <AppErrorAlert data-testid="manufacturer-load-failed">
            This manufacturer could not be loaded. Please refresh the page or try again shortly.
        </AppErrorAlert>
    }
    else if (_notFound)
    {
        <MudAlert Severity="Severity.Warning" Class="mb-4" data-testid="manufacturer-not-found">
            No manufacturer found for <code>@Key</code>.
            <MudLink Href="/documents">Browse all documents</MudLink>
        </MudAlert>
    }
    else
    {
        <AppPageHeader Title="@_displayName"
                       Subtitle="@($"{_rows.Count} machine{(_rows.Count == 1 ? "" : "s")} in the catalog.")" />

        <MudStack Row="true" Spacing="2" Class="mb-4">
            <AppStatusChip Color="Color.Default">@_rows.Count machines</AppStatusChip>
            <AppStatusChip Color="Color.Default">@_totalDocs documents</AppStatusChip>
            <AppStatusChip Color="Color.Default">@_manuals with manuals</AppStatusChip>
        </MudStack>

        @if (_totalDocs > 0)
        {
            <MudLink Href="@($"/documents?manufacturer={Uri.EscapeDataString(_displayName!)}")"
                     Class="d-block mb-4" data-testid="manufacturer-browse-docs">
                Browse all @_totalDocs documents for @_displayName →
            </MudLink>
        }

        <AppDataGrid T="GameRow" Items="@_rows" data-testid="manufacturer-games-table">
            <Columns>
                <TemplateColumn Title="Game" Sortable="true"
                                SortBy="@(new Func<GameRow, object>(r => r.Title))"
                                InitialDirection="SortDirection.Ascending">
                    <CellTemplate>@context.Item.Title</CellTemplate>
                </TemplateColumn>
                <TemplateColumn Title="Year" Sortable="true"
                                SortBy="@(new Func<GameRow, object>(r => r.Year ?? 0))">
                    <CellTemplate>@(context.Item.Year?.ToString() ?? "—")</CellTemplate>
                </TemplateColumn>
                <TemplateColumn Title="Edition">
                    <CellTemplate>@(context.Item.Edition ?? "—")</CellTemplate>
                </TemplateColumn>
                <TemplateColumn Title="Documents" Sortable="true"
                                SortBy="@(new Func<GameRow, object>(r => r.DocCount))">
                    <CellTemplate>
                        @if (context.Item.DocCount > 0)
                        {
                            <MudLink Href="@($"/documents?manufacturer={Uri.EscapeDataString(_displayName!)}&game={Uri.EscapeDataString(context.Item.Title)}")">
                                @context.Item.DocCount
                            </MudLink>
                        }
                        else if (_statsFailed)
                        {
                            <MudText Typo="Typo.body2" Color="Color.Secondary">—</MudText>
                        }
                        else
                        {
                            <MudText Typo="Typo.body2">0</MudText>
                        }
                    </CellTemplate>
                </TemplateColumn>
                <TemplateColumn Title="Manual">
                    <CellTemplate>
                        @if (context.Item.HasManual)
                        {
                            <AppStatusChip Color="Color.Success">Yes</AppStatusChip>
                        }
                    </CellTemplate>
                </TemplateColumn>
            </Columns>
        </AppDataGrid>
    }
</MudContainer>

@code {
    [Parameter] public string Key { get; set; } = default!;

    private sealed record GameRow(string Title, int? Year, string? Edition, int DocCount, bool HasManual);

    private List<GameRow> _rows = [];
    private string? _displayName;
    private int _totalDocs;
    private int _manuals;
    private bool _loading = true;
    private bool _loadFailed;
    private bool _notFound;
    private bool _statsFailed;

    protected override async Task OnInitializedAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Read 1 — authoritative games list (single-partition by manufacturer key).
        List<Machine> machines = [];
        try
        {
            await foreach (var m in Machines.StreamByManufacturerAsync(Key, cts.Token))
                machines.Add(m);
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Manufacturer page: machine stream timed out for '{Key}'.", Key);
            _loadFailed = true;
            _loading = false;
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Manufacturer page: machine stream failed for '{Key}'.", Key);
            _loadFailed = true;
            _loading = false;
            return;
        }

        if (machines.Count == 0)
        {
            _notFound = true;
            _loading = false;
            return;
        }

        _displayName = machines[0].ManufacturerDisplayName;

        // Read 2 — per-machine doc counts (section-scoped). Null rollup = OPDB-only
        // (zero counts); a throw degrades counts to "—" without blanking the list.
        IReadOnlyDictionary<string, MachineDocStats> statsByMachine =
            new Dictionary<string, MachineDocStats>();
        try
        {
            var rollup = await Stats.GetByManufacturerAsync(Key, cts.Token);
            if (rollup is not null)
                statsByMachine = rollup.Machines.ToDictionary(m => m.MachineId);
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Manufacturer page: catalog-stats read timed out for '{Key}'.", Key);
            _statsFailed = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Manufacturer page: catalog-stats read failed for '{Key}'.", Key);
            _statsFailed = true;
        }

        _rows = machines
            .Select(m =>
            {
                statsByMachine.TryGetValue(m.Id, out var s);
                return new GameRow(
                    Title:     m.Title,
                    Year:      m.Year,
                    Edition:   m.EditionLabel,
                    DocCount:  s?.DocCount ?? 0,
                    HasManual: s?.HasManual ?? false);
            })
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _totalDocs = _rows.Sum(r => r.DocCount);
        _manuals = _rows.Count(r => r.HasManual);
        _loading = false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~ManufacturersPageTests"`
Expected: PASS (all 5).

- [ ] **Step 5: Commit**

```bash
WT="c:/earlybird/PinballWizard/.worktrees/manufacturer-detail-page"
git -C "$WT" add src/PinballWizard.Web/Components/Pages/Manufacturers.razor tests/PinballWizard.Web.Tests/Components/Pages/ManufacturersPageTests.cs
git -C "$WT" -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(web) public /manufacturers/{key} detail page — games + grouped doc counts"
```

---

### Task 3: Admin nav — Manufacturers tab

**Files:**
- Modify: `src/PinballWizard.Web/Components/Layout/AdminLayout.razor` (the `AdminNav` list)
- Test: `tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs`

**Interfaces:**
- Consumes: nothing new. Adds a `NavRailItem("/admin/manufacturers", "Manufacturers", Icons.Material.Filled.Factory)` between **Sources** and **Machines**.

- [ ] **Step 1: Write the failing test**

Add to `tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs` (new `[Fact]` in the existing class — match the file's existing render harness/usings):

```csharp
    [Fact]
    public void AdminNav_IncludesManufacturersLink()
    {
        var cut = RenderAdminLayout(); // use the file's existing render helper

        cut.Find("a[href='/admin/manufacturers']");
    }
```

If the file has no shared render helper, mirror the render setup already used by the other facts in `AdminLayoutTests.cs`. Do not invent a new harness.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminLayoutTests.AdminNav_IncludesManufacturersLink"`
Expected: FAIL — no `/admin/manufacturers` anchor in the rendered nav.

- [ ] **Step 3: Add the nav item**

In `src/PinballWizard.Web/Components/Layout/AdminLayout.razor`, insert into the `AdminNav` collection immediately after the Sources line:

```csharp
        new NavRailItem("/admin/sources",         "Sources",         Icons.Material.Filled.Source),
        new NavRailItem("/admin/manufacturers",   "Manufacturers",   Icons.Material.Filled.Factory),
        new NavRailItem("/admin/machines",        "Machines",        Icons.Material.Filled.SportsBaseball),
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminLayoutTests"`
Expected: PASS (new fact + existing facts still green).

- [ ] **Step 5: Commit**

```bash
WT="c:/earlybird/PinballWizard/.worktrees/manufacturer-detail-page"
git -C "$WT" add src/PinballWizard.Web/Components/Layout/AdminLayout.razor tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs
git -C "$WT" -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(web) admin nav: Manufacturers tab → /admin/manufacturers"
```

---

### Task 4: Link fan-out to the focused admin + docs surfaces

Replace inline manufacturer text/links with `<ManufacturerLink>` at the focused set. Each sub-step is small; commit once at the end since they share one intent.

**Files:**
- Modify: `AdminManufacturers.razor`, `AdminMachines.razor`, `AdminMachineDetail.razor`, `DocumentList.razor`, `DocumentDetail.razor`
- Test: extend `AdminManufacturersTests.cs` (one assertion) as the representative fan-out test.

**Interfaces:**
- Consumes: `ManufacturerLink` (Task 1), route `/manufacturers/{key}` (Task 2).

- [ ] **Step 1: Update the representative test (AdminManufacturers repoint)**

In `tests/PinballWizard.Web.Tests/Components/Admin/AdminManufacturersTests.cs`, change the link assertion in `Populated_RendersRowWithNameStatusCountAndSourceLink` from the source route to the manufacturer detail route, and rename it for clarity:

```csharp
    [Fact]
    public async Task Populated_RendersRowWithNameStatusCountAndManufacturerLink()
    {
        _machines.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("stern", "Stern Pinball"), M("stern", "Stern Pinball")));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(Source("stern", "Stern Pinball", enabled: true)));

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var cells = cut.Find("[data-testid='manufacturers-table'] tbody tr").QuerySelectorAll("td");
        Assert.Contains("Stern Pinball", cells[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Enabled", cells[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("2", cells[2].TextContent.Trim());
        cut.Find("a[href='/manufacturers/stern']");   // now links to the detail page
    }
```

Also update `NoSourceForKey_PlainTextDisplayName_NoSourceLink`: OPDB-only rows now DO render a link (to the detail page). Rename to `NoSourceForKey_StillLinksToManufacturerDetail` and change its final assertions:

```csharp
        var table = cut.Find("[data-testid='manufacturers-table']");
        Assert.Contains("Williams", table.TextContent, StringComparison.Ordinal);
        cut.Find("a[href='/manufacturers/williams']");   // detail page works for OPDB-only too
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminManufacturersTests"`
Expected: FAIL — rows still link to `/admin/sources/...`, not `/manufacturers/...`.

- [ ] **Step 3: Repoint AdminManufacturers name column**

In `src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor`, replace the `CellTemplate` of the Manufacturer column (the `if (context.Item.HasSource) MudLink … else MudText` block) with a single unconditional link:

```razor
                    <CellTemplate>
                        <ManufacturerLink ManufacturerKey="@context.Item.Key" DisplayName="@context.Item.DisplayName" />
                    </CellTemplate>
```

(The `HasSource` flag stays used by the Status column; only the name cell changes. `ManufacturerLink` resolves via the existing `Components/Shared` namespace import — confirm `_Imports.razor` already imports `PinballWizard.Web.Components.Shared`; the `App*` wrappers used across pages prove it does.)

- [ ] **Step 4: Run the AdminManufacturers tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminManufacturersTests"`
Expected: PASS.

- [ ] **Step 5: Apply the same link to the remaining call-sites**

For each file, replace the inline manufacturer display (a `MudText`, `PropertyColumn`, or raw text showing the manufacturer name) with `<ManufacturerLink ManufacturerKey="@<key>" DisplayName="@<displayName>" />`, using the key/display-name already in scope:

- `src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor` — manufacturer column/field. Key = the machine's `PartitionKey`, display = `ManufacturerDisplayName`.
- `src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor` — manufacturer field. **RE-CHECK for the parallel session's edits before touching (`git -C "$WT" fetch` + inspect); if it now conflicts, coordinate rather than force.** Key = `_machine.PartitionKey`, display = `_machine.ManufacturerDisplayName`.
- `src/PinballWizard.Web/Components/Shared/DocumentList.razor` — change `<PropertyColumn Property="x => x.Manufacturer" Title="Manufacturer" />` to a `TemplateColumn` rendering `ManufacturerLink`. **Precondition:** `DocumentListItem` must expose a manufacturer *key* (not just display name). If it exposes only `Manufacturer` (display name), do NOT guess a key — skip this call-site, leave the `PropertyColumn`, and note it as a follow-up in the PR description (the display-name→key map is not available client-side here). Verify by reading `DocumentListItem`'s definition first.
- `src/PinballWizard.Web/Components/Pages/DocumentDetail.razor` — manufacturer field. Same precondition: only linkify if a manufacturer key is in scope; otherwise leave as text and note it.

For any skipped call-site, record it explicitly in the PR description (honest scope, per Invariant #17 spirit — no silent omissions).

- [ ] **Step 6: Build + run the full Web test project**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj`
Expected: PASS (no regressions from the fan-out edits).

- [ ] **Step 7: Commit**

```bash
WT="c:/earlybird/PinballWizard/.worktrees/manufacturer-detail-page"
git -C "$WT" add src/PinballWizard.Web tests/PinballWizard.Web.Tests
git -C "$WT" -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(web) link manufacturer mentions to /manufacturers/{key} across admin + docs"
```

---

## Final verification (before PR)

- [ ] Full CI-equivalent suite from the worktree:
  `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
- [ ] `/local-review` (qualitative) — treat 🔴 as blocking.
- [ ] `/standards-audit` (mechanical gate) — must pass; confirm no new cross-partition query was introduced (none is — `StreamByManufacturerAsync` is single-partition, rollup is a point read), so no ADR-0036 allow-list change is expected.
- [ ] PR via `gh pr create`; add + verify the `claude-code` label; put the full PR URL in the response; record `/local-review` outcome and any skipped fan-out call-sites in the description.

## Self-review (completed against the spec)

- **Spec coverage:** nav tab → Task 3; detail page games → Task 2; grouped doc counts + browse-all → Task 2; link fan-out → Tasks 1+4; security boundary → enforced by page reading only catalog/rollup + linking to public /documents (Task 2). All spec sections mapped.
- **Placeholder scan:** no TBD/TODO; every code step shows full code; the one conditional (DocumentList/DocumentDetail linkify) has an explicit verify-or-skip rule rather than a guess, per the no-guessing rule.
- **Type consistency:** `Machine.Id`/`PartitionKey`/`ManufacturerDisplayName`/`Title`/`Year`/`EditionLabel` verified against `Machine.cs`; `MachineDocStats(MachineId, Title, EditionLabel, GroupId, Year, IsOpdbOnly, DocCount, DocTypeCounts, HasManual)` verified against `ManufacturerCatalogStats.cs`; join `MachineDocStats.MachineId == Machine.Id` verified against `AdminMachineDetail.razor`. `ManufacturerLink` param names (`ManufacturerKey`, `DisplayName`, `Typo`) consistent across Tasks 1 and 4.
