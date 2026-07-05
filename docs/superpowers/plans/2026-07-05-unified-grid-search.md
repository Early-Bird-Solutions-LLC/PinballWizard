# Unified AI Grid Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the AI-driven "GridSearch" natural-language box the single, consistent search mechanism on every data grid in the app, fix the two silent-no-op bugs it currently has (stale agent-prompt schema, dead semantic-search branch), and make every grid default to a 10-row page size.

**Architecture:** `AppDataGrid<T>` (the shared MudDataGrid wrapper) already bakes in the `GridSearch` component via `EnableAiSearch=true`. This plan (1) fixes `AppDataGrid`'s filtering logic to actually use the AI response's semantic-search branch and to clear correctly, (2) fixes/extends the agent prompt (`Ai/Agents/GridSearch.md`) so every grid context has an accurate column schema, (3) migrates every remaining raw-`MudDataGrid` admin page to `AppDataGrid`, and (4) removes `DocumentList.razor`'s redundant legacy filter UI in favor of GridSearch alone.

**Tech Stack:** Blazor Server (MudBlazor 9.x `MudDataGrid`), Microsoft Agent Framework (`Microsoft.Agents.AI` — `AIAgent`/`IFoundryAgentFactory`), bUnit for component tests, xUnit + NSubstitute for unit tests.

## Global Constraints

- Every grid uses `AppDataGrid`, not raw `MudDataGrid`, except `AdminSourceDetail` (documented ADR-0046 exception — `HierarchyColumn` + master-detail rows aren't compatible with `AppDataGrid`'s attribute splatting; not touched by this plan).
- Every migrated grid drops any hardcoded `RowsPerPage="N"` so it falls through to `AppDataGrid`'s own `RowsPerPage="@(RowsPerPage ?? Prefs.PageSize)"` — the shared `IUserPreferencesService.PageSize` default (10 in production).
- Every migrated grid drops any explicit `<PagerContent><MudDataGridPager T="..." /></PagerContent>` child block — it is redundant with `AppDataGrid`'s own default pager (`ShowPager=true` → renders a bare `<MudDataGridPager T="T" />` automatically). No page in this plan needs a custom `ShowPager="false"` override.
- Every new/changed `SearchContext` value follows the existing kebab-case convention (`admin-machines`, `admin-jobs`, `admin-document-triage`).
- Whenever a page's `SearchContext` value is new or its row schema changes, the corresponding `Ai/Agents/GridSearch.md` section is added/corrected in the *same task* — this is precisely the class of bug this whole plan exists to fix (a stale prompt silently no-ops filters), so schema and prompt must never land in separate commits.
- Personal-repo commit identity: every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`, no Claude attribution trailer (matches this repo's established convention).

---

### Task 1: Fix the bUnit test harness's page-size mock (25 → 10)

**Files:**
- Modify: `tests/PinballWizard.Web.Tests/AsyncBunitContext.cs:23-28`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminManufacturersTests.cs:180-196`

**Interfaces:**
- Consumes: `PinballWizard.Web.Services.IUserPreferencesService.PageSize` (production default: `10`, per `UserPreferencesService.cs:53`)
- Produces: every subsequent test in this plan that renders a migrated grid without an explicit `RowsPerPage` override will see 10 rows/page, matching production.

Every bUnit test in the whole suite shares `AsyncBunitContext`, which currently mocks `IUserPreferencesService.PageSize` to return `25` — silently diverging from the real production default of `10`. The only existing test that actually depends on this mocked value is `AdminManufacturersTests.PagingAt25_RendersOnlyFirstPageWhenMoreThan25Manufacturers` (confirmed by repo-wide search — no other test in `PinballWizard.Web.Tests` asserts a specific pagination row count tied to 25). Fix both together so nothing is left red.

- [ ] **Step 1: Change the mocked default from 25 to 10**

In `tests/PinballWizard.Web.Tests/AsyncBunitContext.cs`, change:

```csharp
        // ADR-0008: register IUserPreferencesService (with 25-row default) so components
        // like AppDataGrid and MudDataGrid can resolve their RowsPerPage parameter
        // in unit tests.
        var prefs = NSubstitute.Substitute.For<PinballWizard.Web.Services.IUserPreferencesService>();
        prefs.PageSize.Returns(25);
```

to:

```csharp
        // Register IUserPreferencesService with the SAME default as production
        // (UserPreferencesService.PageSize = 10) so components like AppDataGrid and
        // MudDataGrid resolve RowsPerPage the same way in tests as in the real app.
        var prefs = NSubstitute.Substitute.For<PinballWizard.Web.Services.IUserPreferencesService>();
        prefs.PageSize.Returns(10);
```

- [ ] **Step 2: Update the one dependent test to match the new page size**

In `tests/PinballWizard.Web.Tests/Components/Admin/AdminManufacturersTests.cs`, replace the existing test:

```csharp
    [Fact]
    public async Task PagingAt25_RendersOnlyFirstPageWhenMoreThan25Manufacturers()
    {
        // 26 distinct manufacturers — page 1 should show exactly 25 rows; pager footer must render.
        var machines = Enumerable.Range(1, 26)
            .Select(i => M($"mfr{i:D2}", $"Manufacturer {i:D2}"))
            .ToArray();
        _machines.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream(machines));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var rows = cut.FindAll("[data-testid='manufacturers-table'] tbody tr");
        Assert.Equal(25, rows.Count);
        cut.Find(".mud-table-pagination");
    }
```

with:

```csharp
    [Fact]
    public async Task PagingAt10_RendersOnlyFirstPageWhenMoreThan10Manufacturers()
    {
        // 11 distinct manufacturers — page 1 should show exactly 10 rows (the shared
        // Prefs.PageSize default), pager footer must render.
        var machines = Enumerable.Range(1, 11)
            .Select(i => M($"mfr{i:D2}", $"Manufacturer {i:D2}"))
            .ToArray();
        _machines.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream(machines));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var rows = cut.FindAll("[data-testid='manufacturers-table'] tbody tr");
        Assert.Equal(10, rows.Count);
        cut.Find(".mud-table-pagination");
    }
```

- [ ] **Step 3: Run the full Web.Tests suite to confirm nothing else broke**

Run: `dotnet test tests/PinballWizard.Web.Tests --nologo`
Expected: all tests pass (no regressions from the mock change — this was the only dependent test, per the repo-wide search performed during planning).

- [ ] **Step 4: Commit**

```bash
git add tests/PinballWizard.Web.Tests/AsyncBunitContext.cs tests/PinballWizard.Web.Tests/Components/Admin/AdminManufacturersTests.cs
git commit -m "test(web) align bUnit page-size mock with production default (10, not 25)"
```

---

### Task 2: Fix `AppDataGrid`'s semantic-search dead branch and the clear-filter bug

**Files:**
- Modify: `src/PinballWizard.Web/Components/Shared/AppDataGrid.razor`
- Modify: `src/PinballWizard.Web/Components/Shared/GridSearch.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Shared/AppDataGridTests.cs`

**Interfaces:**
- Consumes: `PinballWizard.Application.Ai.GridSearch.GridSearchResponse(IReadOnlyList<GridFilter> Filters, string Explanation, bool IsSemanticSearch = false, string? SemanticQuery = null)`, `GridFilter(string Column, string Operator, string Value)` — both already exist, unchanged.
- Produces: `AppDataGrid<T>.FilterFunc` now also matches on `SemanticQuery` when `IsSemanticSearch` is true; `GridSearch.ClearFeedback` is now `async Task` and notifies the parent via `OnFiltersApplied`.

Two bugs, fixed together since they touch the same filtering code path:

1. `HandleAiFilters` only reads `response.Filters` — a semantic query (`IsSemanticSearch=true`, `Filters=[]`) results in an empty filter list, so `FilterFunc` shows every row unfiltered while the UI's explanation banner claims a search happened.
2. `GridSearch.ClearFeedback()` resets its own local state but never calls `OnFiltersApplied` again, so `AppDataGrid._currentFilters` stays applied after the user dismisses the banner.

- [ ] **Step 1: Write the failing tests in `AppDataGridTests.cs`**

Add these using directives and test-only records at the top of the file (right after the existing `private sealed record Row(string Name);` on line 18):

```csharp
    private sealed record ThemedRow(string Name, List<string> Themes);
```

Then update `RenderGrid` to support the new `ThemedRow` column set — add a second render helper right after the existing `RenderGrid` method (after line 45):

```csharp
    private IRenderedComponent<IComponent> RenderThemedGrid(IEnumerable<ThemedRow> items)
    {
        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDataGrid<ThemedRow>>(1);
            builder.AddAttribute(2, "Items", items);
            builder.AddAttribute(6, "Columns", (RenderFragment)(b =>
            {
                b.OpenComponent<PropertyColumn<ThemedRow, string>>(0);
                b.AddAttribute(1, "Property", (System.Linq.Expressions.Expression<Func<ThemedRow, string>>)(r => r.Name));
                b.AddAttribute(2, "Title", "Name");
                b.CloseComponent();
            }));
            builder.CloseComponent();
        });
    }

    private static PinballWizard.Application.Ai.GridSearch.GridSearchResponse SemanticResponse(string query) =>
        new([], $"Searching for {query}", IsSemanticSearch: true, SemanticQuery: query);

    private static PinballWizard.Application.Ai.GridSearch.GridSearchResponse StructuralResponse(
        string column, string op, string value) =>
        new([new PinballWizard.Application.Ai.GridSearch.GridFilter(column, op, value)], "Filtered", false, null);
```

Now add the new test methods at the end of the class, before the closing brace:

```csharp
    [Fact]
    public void SemanticSearch_MatchesOnStringProperty()
    {
        var items = new[] { new Row("Godzilla Pro"), new Row("Iron Man Pro") };
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDataGrid<Row>>(1);
            builder.AddAttribute(2, "Items", items);
            builder.AddAttribute(6, "Columns", (RenderFragment)(b =>
            {
                b.OpenComponent<PropertyColumn<Row, string>>(0);
                b.AddAttribute(1, "Property", (System.Linq.Expressions.Expression<Func<Row, string>>)(r => r.Name));
                b.AddAttribute(2, "Title", "Name");
                b.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var grid = cut.FindComponent<AppDataGrid<Row>>();

        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [SemanticResponse("godzilla")]));

        Assert.Contains("Godzilla Pro", cut.Markup);
        Assert.DoesNotContain("Iron Man Pro", cut.Markup);
    }

    [Fact]
    public void SemanticSearch_MatchesOnIEnumerableStringProperty()
    {
        var items = new[]
        {
            new ThemedRow("Alien Invasion", ["sci-fi", "horror"]),
            new ThemedRow("Wild West Showdown", ["western"]),
        };
        var cut = RenderThemedGrid(items);
        var grid = cut.FindComponent<AppDataGrid<ThemedRow>>();

        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [SemanticResponse("sci-fi")]));

        Assert.Contains("Alien Invasion", cut.Markup);
        Assert.DoesNotContain("Wild West Showdown", cut.Markup);
    }

    [Fact]
    public void SemanticSearch_CaseInsensitive_NoMatch_ShowsNoRows()
    {
        var items = new[] { new Row("Godzilla Pro") };
        var cut = RenderGrid(items);
        var grid = cut.FindComponent<AppDataGrid<Row>>();

        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [SemanticResponse("nonexistent-theme")]));

        Assert.DoesNotContain("Godzilla Pro", cut.Markup);
    }

    [Fact]
    public void StructuralFilter_StillAppliesWhenNotSemanticSearch()
    {
        var items = new[] { new Row("Alpha"), new Row("Beta") };
        var cut = RenderGrid(items);
        var grid = cut.FindComponent<AppDataGrid<Row>>();

        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [StructuralResponse("Name", "equals", "Alpha")]));

        Assert.Contains("Alpha", cut.Markup);
        Assert.DoesNotContain("Beta", cut.Markup);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppDataGridTests" --nologo`
Expected: `SemanticSearch_MatchesOnStringProperty`, `SemanticSearch_MatchesOnIEnumerableStringProperty`, and `SemanticSearch_CaseInsensitive_NoMatch_ShowsNoRows` FAIL (both rows render — semantic query currently does nothing). `StructuralFilter_StillAppliesWhenNotSemanticSearch` PASSES already (structural filtering already works).

- [ ] **Step 3: Implement the semantic-match fix in `AppDataGrid.razor`**

Replace the `@code` block's filter section (from `private List<GridFilter> _currentFilters = [];` through the end of `ApplyOperator`) with:

```csharp
    private List<GridFilter> _currentFilters = [];
    private bool _isSemanticSearch;
    private string? _semanticQuery;
    private Func<T, bool> _quickFilter => FilterFunc;

    private void HandleAiFilters(GridSearchResponse response)
    {
        _currentFilters = response.Filters.ToList();
        _isSemanticSearch = response.IsSemanticSearch;
        _semanticQuery = response.SemanticQuery;
        StateHasChanged();
    }

    private bool FilterFunc(T item)
    {
        if (_isSemanticSearch && !string.IsNullOrWhiteSpace(_semanticQuery))
        {
            return MatchesSemanticQuery(item, _semanticQuery);
        }

        if (_currentFilters.Count == 0) return true;

        foreach (var filter in _currentFilters)
        {
            var prop = typeof(T).GetProperty(filter.Column);
            if (prop == null) continue;

            var val = prop.GetValue(item);
            if (!ApplyOperator(val, filter.Operator, filter.Value))
                return false;
        }
        return true;
    }

    // Generic semantic-ish match: concatenate every public string property and every
    // public IEnumerable<string> property's joined values into one case-insensitive
    // haystack, then substring-match the query. Deliberately reflection-based (same
    // approach FilterFunc/ApplyOperator already use for structural filters) so it
    // works for any row type without per-page wiring — Title, Manufacturer, Edition,
    // and any future row type's string-ish properties are picked up automatically.
    private static bool MatchesSemanticQuery(T item, string query)
    {
        foreach (var prop in typeof(T).GetProperties())
        {
            var value = prop.GetValue(item);
            if (value is string s)
            {
                if (s.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (value is System.Collections.Generic.IEnumerable<string> strings)
            {
                if (strings.Any(x => x is not null && x.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    private static bool ApplyOperator(object? value, string op, string filterValue)
    {
        if (value == null) return string.IsNullOrWhiteSpace(filterValue) && op == "equals";

        var valStr = value.ToString() ?? "";

        switch (op.ToLowerInvariant())
        {
            case "contains":
                return valStr.Contains(filterValue, StringComparison.OrdinalIgnoreCase);
            case "equals":
                return valStr.Equals(filterValue, StringComparison.OrdinalIgnoreCase);
            case "gt":
                if (double.TryParse(valStr, out var vGt) && double.TryParse(filterValue, out var fGt))
                    return vGt > fGt;
                if (DateTime.TryParse(valStr, out var dGt) && DateTime.TryParse(filterValue, out var fdGt))
                    return dGt > fdGt;
                break;
            case "lt":
                if (double.TryParse(valStr, out var vLt) && double.TryParse(filterValue, out var fLt))
                    return vLt < fLt;
                if (DateTime.TryParse(valStr, out var dLt) && DateTime.TryParse(filterValue, out var fdLt))
                    return dLt < fdLt;
                break;
            case "ge":
                if (double.TryParse(valStr, out var vGe) && double.TryParse(filterValue, out var fGe))
                    return vGe >= fGe;
                if (DateTime.TryParse(valStr, out var dGe) && DateTime.TryParse(filterValue, out var fdGe))
                    return dGe >= fdGe;
                break;
            case "le":
                if (double.TryParse(valStr, out var vLe) && double.TryParse(filterValue, out var fLe))
                    return vLe <= fLe;
                if (DateTime.TryParse(valStr, out var dLe) && DateTime.TryParse(filterValue, out var fdLe))
                    return dLe <= fdLe;
                break;
        }

        return false;
    }
```

(The `PagerContent`/`_pagerContent` property below this block, and everything else in the file, is unchanged.)

- [ ] **Step 4: Run the tests again to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppDataGridTests" --nologo`
Expected: all `AppDataGridTests` PASS.

- [ ] **Step 5: Write the failing test for the ClearFeedback bug**

This is a `GridSearch.razor`-level behavior, tested through `AppDataGrid` since that's what owns `_currentFilters`. Add to `AppDataGridTests.cs`:

```csharp
    [Fact]
    public void ClearFeedback_ResetsAppliedFilter()
    {
        var items = new[] { new Row("Alpha"), new Row("Beta") };
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDataGrid<Row>>(1);
            builder.AddAttribute(2, "Items", items);
            builder.AddAttribute(3, "SearchContext", "test-context");
            builder.AddAttribute(6, "Columns", (RenderFragment)(b =>
            {
                b.OpenComponent<PropertyColumn<Row, string>>(0);
                b.AddAttribute(1, "Property", (System.Linq.Expressions.Expression<Func<Row, string>>)(r => r.Name));
                b.AddAttribute(2, "Title", "Name");
                b.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var gridSearch = cut.FindComponent<PinballWizard.Web.Components.Shared.GridSearch>();

        // Apply a structural filter directly on the grid — this is the same state
        // GridSearch would produce by calling its bound OnFiltersApplied callback,
        // so exercising it here (rather than driving the search box's own UI) keeps
        // the test focused on the ClearFeedback → OnFiltersApplied contract.
        var grid = cut.FindComponent<AppDataGrid<Row>>();
        grid.InvokeAsync(() => grid.Instance.GetType()
            .GetMethod("HandleAiFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid.Instance, [StructuralResponse("Name", "equals", "Alpha")]));
        Assert.DoesNotContain("Beta", cut.Markup);

        // Dismissing the feedback banner (ClearFeedback) must un-filter the grid.
        gridSearch.InvokeAsync(() => gridSearch.Instance.GetType()
            .GetMethod("ClearFeedback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(gridSearch.Instance, null));

        Assert.Contains("Beta", cut.Markup);
    }
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~ClearFeedback_ResetsAppliedFilter" --nologo`
Expected: FAIL — "Beta" is still absent after `ClearFeedback` (the bug).

- [ ] **Step 7: Fix `ClearFeedback` in `GridSearch.razor`**

Replace:

```csharp
    private void ClearFeedback()
    {
        _lastResponse = null;
        _query = "";
        StateHasChanged();
    }
```

with:

```csharp
    private async Task ClearFeedback()
    {
        _lastResponse = null;
        _query = "";
        await OnFiltersApplied.InvokeAsync(new GridSearchResponse([], "", false, null));
        StateHasChanged();
    }
```

And update the close-button call site (in the markup, the `MudIconButton` with `Icon="@Icons.Material.Filled.Close"`) from `OnClick="ClearFeedback"` — this already works unchanged for an `async Task`-returning `OnClick` handler, MudBlazor accepts both `Action` and `Func<Task>` for `OnClick`, so no markup change is needed.

- [ ] **Step 8: Run the test again to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~ClearFeedback_ResetsAppliedFilter" --nologo`
Expected: PASS.

- [ ] **Step 9: Run the full AppDataGridTests file once more for a clean pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppDataGridTests" --nologo`
Expected: all pass, including the 3 pre-existing tests (`RendersItemsAsRows`, `ShowPagerTrue_RendersPagination`, `ShowPagerFalse_HidesPagination`, `SplatsDataTestId`).

- [ ] **Step 10: Commit**

```bash
git add src/PinballWizard.Web/Components/Shared/AppDataGrid.razor src/PinballWizard.Web/Components/Shared/GridSearch.razor tests/PinballWizard.Web.Tests/Components/Shared/AppDataGridTests.cs
git commit -m "fix(web) wire semantic-search matching and fix ClearFeedback filter reset in AppDataGrid"
```

---

### Task 3: Add `GridSearchServiceTests` (currently zero coverage)

**Files:**
- Create: `tests/PinballWizard.Application.Tests/Ai/GridSearch/GridSearchServiceTests.cs`

**Interfaces:**
- Consumes: `PinballWizard.Application.Ai.GridSearch.GridSearchService(IFoundryAgentFactory, ILogger<GridSearchService>)`, `IFoundryAgentFactory.GetAgent(string)` returning `Microsoft.Agents.AI.AIAgent`.
- Produces: nothing new — this task is pure test coverage of existing production code (`GridSearchService.cs`, unchanged by this task).

`AIAgent`'s public `RunAsync` overloads are non-virtual convenience wrappers around a protected abstract `RunCoreAsync` — confirmed via reflection and the existing `CapturingAgent` fake pattern already used in `tests/PinballWizard.Infrastructure.Tests/Ai/AiRouterMultiTurnTests.cs`. This task writes a small, self-contained fake agent scoped to `Application.Tests` (not shared with Infrastructure.Tests — different test project, and the existing `CapturingAgent` supports streaming/session behavior this test doesn't need).

- [ ] **Step 1: Write the fake agent and the first failing test**

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.GridSearch;
using Xunit;

namespace PinballWizard.Application.Tests.Ai.GridSearch;

public sealed class GridSearchServiceTests
{
    // AIAgent's public RunAsync overloads are non-virtual; the override point is the
    // protected RunCoreAsync (confirmed via reflection: RunCoreAsync is the sole
    // virtual+abstract member). Mirrors the CapturingAgent pattern already used in
    // PinballWizard.Infrastructure.Tests/Ai/AiRouterMultiTurnTests.cs, scoped down to
    // exactly what GridSearchService needs (single non-streaming RunAsync call).
    private sealed class FakeAgent : AIAgent
    {
        private readonly string? _responseText;
        private readonly Exception? _throws;

        public static FakeAgent Returning(string text) => new(text, null);
        public static FakeAgent Throwing(Exception ex) => new(null, ex);

        private FakeAgent(string? responseText, Exception? throws)
        {
            _responseText = responseText;
            _throws = throws;
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_throws is not null) throw _throws;
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, _responseText)));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("GridSearchService does not use streaming.");

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException("GridSearchService does not use sessions.");

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException("GridSearchService does not use sessions.");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException("GridSearchService does not use sessions.");
    }

    private static (GridSearchService Service, IFoundryAgentFactory Factory) MakeService(AIAgent agent)
    {
        var factory = Substitute.For<IFoundryAgentFactory>();
        factory.GetAgent(AgentName.GridSearch).Returns(agent);
        var service = new GridSearchService(factory, NullLogger<GridSearchService>.Instance);
        return (service, factory);
    }

    [Fact]
    public async Task SearchAsync_WellFormedJson_ParsesFiltersAndExplanation()
    {
        var json = """
            {"filters":[{"column":"Manufacturer","operator":"equals","value":"Bally"}],"explanation":"Bally machines.","isSemanticSearch":false,"semanticQuery":null}
            """;
        var (service, _) = MakeService(FakeAgent.Returning(json));

        var result = await service.SearchAsync("Bally machines", "admin-machines", CancellationToken.None);

        Assert.Single(result.Filters);
        Assert.Equal("Manufacturer", result.Filters[0].Column);
        Assert.Equal("Bally machines.", result.Explanation);
        Assert.False(result.IsSemanticSearch);
    }

    [Fact]
    public async Task SearchAsync_MarkdownFencedJson_ExtractsAndParses()
    {
        var fenced = """
            Here's the filter:
            ```json
            {"filters":[],"explanation":"Semantic search for sci-fi.","isSemanticSearch":true,"semanticQuery":"sci-fi"}
            ```
            """;
        var (service, _) = MakeService(FakeAgent.Returning(fenced));

        var result = await service.SearchAsync("sci-fi games", "admin-machines", CancellationToken.None);

        Assert.True(result.IsSemanticSearch);
        Assert.Equal("sci-fi", result.SemanticQuery);
    }

    [Fact]
    public async Task SearchAsync_NonJsonResponse_ReturnsExplanatoryResponse_NotException()
    {
        var (service, _) = MakeService(FakeAgent.Returning("I don't understand the query."));

        var result = await service.SearchAsync("???", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
        Assert.Contains("couldn't parse", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_AgentThrows_ReturnsExplanatoryResponse_NotException()
    {
        var (service, _) = MakeService(FakeAgent.Throwing(new InvalidOperationException("Foundry unavailable")));

        var result = await service.SearchAsync("Bally machines", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
        Assert.Contains("error occurred", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_UsesGridSearchAgentName()
    {
        var (service, factory) = MakeService(FakeAgent.Returning("""{"filters":[],"explanation":"x","isSemanticSearch":false,"semanticQuery":null}"""));

        await service.SearchAsync("anything", "admin-machines", CancellationToken.None);

        factory.Received(1).GetAgent(AgentName.GridSearch);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail (or don't compile) first**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~GridSearchServiceTests" --nologo`
Expected: since `GridSearchService.cs` already exists and is unchanged, these should actually PASS immediately — this task is pure coverage-add, not a TDD red/green cycle. If any test fails, it indicates a real behavior mismatch with the assumptions above (e.g. verify `NullLogger<GridSearchService>` resolves, `Microsoft.Extensions.AI` provides `ChatMessage`/`ChatRole`).

- [ ] **Step 3: Confirm all 5 tests pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~GridSearchServiceTests" --nologo`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 4: Commit**

```bash
git add tests/PinballWizard.Application.Tests/Ai/GridSearch/GridSearchServiceTests.cs
git commit -m "test(ai) add GridSearchServiceTests (zero prior coverage)"
```

---

### Task 4: Add `GridSearchClientTests` (currently zero coverage)

**Files:**
- Create: `tests/PinballWizard.Web.Tests/Clients/GridSearchClientTests.cs`

**Interfaces:**
- Consumes: `PinballWizard.Web.Clients.GridSearchClient(HttpClient, ILogger<GridSearchClient>)` implementing `IGridSearchClient.SearchAsync(string query, string gridContext, CancellationToken)`.
- Produces: nothing new — pure test coverage of existing, unchanged production code.

- [ ] **Step 1: Write the failing tests using a fake `HttpMessageHandler`**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Ai.GridSearch;
using PinballWizard.Web.Clients;
using Xunit;

namespace PinballWizard.Web.Tests.Clients;

public sealed class GridSearchClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static GridSearchClient MakeClient(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") },
            NullLogger<GridSearchClient>.Instance);

    [Fact]
    public async Task SearchAsync_EmptyQuery_ShortCircuits_NoHttpCall()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("should not be called"));
        var client = MakeClient(handler);

        var result = await client.SearchAsync("   ", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SearchAsync_SuccessResponse_Deserializes()
    {
        var payload = new GridSearchResponse(
            [new GridFilter("Manufacturer", "equals", "Stern")], "Stern machines.", false, null);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        var client = MakeClient(handler);

        var result = await client.SearchAsync("Stern machines", "admin-machines", CancellationToken.None);

        Assert.Single(result.Filters);
        Assert.Equal("Stern", result.Filters[0].Value);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("q=Stern", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("context=admin-machines", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_HttpException_ReturnsExplanatoryResponse_NotThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var client = MakeClient(handler);

        var result = await client.SearchAsync("Stern machines", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
        Assert.Contains("Failed to connect", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_NonSuccessStatusCode_ReturnsExplanatoryResponse_NotThrows()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = MakeClient(handler);

        var result = await client.SearchAsync("Stern machines", "admin-machines", CancellationToken.None);

        Assert.Empty(result.Filters);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~GridSearchClientTests" --nologo`
Expected: all 4 pass immediately — this task is pure coverage-add of existing, unchanged production code. `GetFromJsonAsync` is verified (via its `dotnet/runtime` source — `FromJsonAsyncCore` calls `response.EnsureSuccessStatusCode()` before deserializing) to throw `HttpRequestException` on a non-success status, which `GridSearchClient`'s existing `catch (Exception ex) when (ex is not OperationCanceledException)` block already catches and turns into the same explanatory response.

- [ ] **Step 3: Commit**

```bash
git add tests/PinballWizard.Web.Tests/Clients/GridSearchClientTests.cs
git commit -m "test(web) add GridSearchClientTests (zero prior coverage)"
```

---

### Task 5: Restore `AppDataGrid` on `AdminDocumentTriage` (revert the accidental regression)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor:52-54,109`
- Modify: `src/PinballWizard.Application/Ai/Agents/GridSearch.md` (verify only — no change needed, already accurate)
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminDocumentTriageTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<DocumentTriageRow>` (existing component, from Task 2).
- Produces: `[data-testid='grid-search-input']` now renders on `/admin/document-triage`.

- [ ] **Step 1: Write the failing test**

Add to `AdminDocumentTriageTests.cs`:

```csharp
    [Fact]
    public async Task AdminDocumentTriage_Renders_GridSearchBox()
    {
        var cut = RenderWithPopover<AdminDocumentTriage>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='grid-search-input']");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminDocumentTriage_Renders_GridSearchBox" --nologo`
Expected: FAIL with `ElementNotFoundException` — the grid-search box isn't present on the current raw `MudDataGrid`.

- [ ] **Step 3: Restore `AppDataGrid` in the razor file**

In `AdminDocumentTriage.razor`, change:

```razor
    <MudDataGrid T="DocumentTriageRow"
                 Items="@_documents"
                 data-testid="admin-document-triage-grid">
```

to:

```razor
    <AppDataGrid T="DocumentTriageRow"
                 Items="@_documents"
                 SearchContext="admin-document-triage"
                 data-testid="admin-document-triage-grid">
```

And change the closing tag on line 109 from `</MudDataGrid>` to `</AppDataGrid>`.

- [ ] **Step 4: Run the test again to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminDocumentTriage_Renders_GridSearchBox" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full AdminDocumentTriageTests file to confirm no regressions**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminDocumentTriageTests" --nologo`
Expected: all pass (the existing grid-sentinel/empty-state/breadcrumb tests are unaffected — `AppDataGrid` forwards `NoRecordsContent`, and the `data-testid` on the outer tag is preserved via attribute splatting).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminDocumentTriageTests.cs
git commit -m "fix(web) restore AppDataGrid on AdminDocumentTriage (accidental regression in 74bd102)"
```

---

### Task 6: Restore `AppDataGrid` on `AdminJobs`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobs.razor:83-138`
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobsTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<JobStatus>` (existing component, from Task 2).
- Produces: `[data-testid='grid-search-input']` now renders on `/admin/jobs`.

- [ ] **Step 1: Write the failing test**

Add to `AdminJobsTests.cs` (reusing whatever `RenderPage(svc)` helper the existing file defines, with a populated `SeedJobs` service so the grid actually renders — the search box only shows in the populated branch, not the loading/empty/error states):

```csharp
    [Fact]
    public async Task AdminJobs_Populated_RendersGridSearchBox()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.ListJobsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(SeedJobs));

        var cut = RenderPage(svc);
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='grid-search-input']");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminJobs_Populated_RendersGridSearchBox" --nologo`
Expected: FAIL — no search box on the current raw `MudDataGrid`.

- [ ] **Step 3: Restore `AppDataGrid` in the razor file**

In `AdminJobs.razor`, change:

```razor
        <MudDataGrid T="JobStatus" Items="@_jobs"
                     RowClick="@(EventCallback.Factory.Create<DataGridRowClickEventArgs<JobStatus>>(this, HandleRowClick))"
                     data-testid="jobs-table">
```

to:

```razor
        <AppDataGrid T="JobStatus" Items="@_jobs"
                     SearchContext="admin-jobs"
                     RowClick="@(EventCallback.Factory.Create<DataGridRowClickEventArgs<JobStatus>>(this, HandleRowClick))"
                     data-testid="jobs-table">
```

And change the closing `</MudDataGrid>` (right before the `}` that ends the `else` block, currently the line right after the `</Columns>` close) to `</AppDataGrid>`.

- [ ] **Step 4: Run the test again to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminJobs_Populated_RendersGridSearchBox" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full AdminJobsTests file**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminJobsTests" --nologo`
Expected: all pass, including the existing row-click-navigates and run-now-dialog tests (both use attributes/handlers that pass through `AppDataGrid`'s `AdditionalAttributes` splatting unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobs.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminJobsTests.cs
git commit -m "fix(web) restore AppDataGrid on AdminJobs (accidental regression in 74bd102)"
```

---

### Task 7: Restore `AppDataGrid` on `AdminMachines` and fix the agent prompt's schema bugs

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor:86-91,188-190,206`
- Modify: `src/PinballWizard.Application/Ai/Agents/GridSearch.md` (`admin-machines` section)
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminMachinesTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<MachineCatalogRow>` (existing component, from Task 2).
- Produces: `[data-testid='grid-search-input']` now renders on `/admin/machines`; the `admin-machines` prompt schema matches the real `MachineCatalogRow` property names.

Two independent bugs in the same section, fixed together: (1) `AppDataGrid` was reverted here too; (2) the `admin-machines` prompt section documents a `Franchise` column that `5870325` deleted from `MachineCatalogRow`, AND documents `Year` (int) when the actual property is `YearLabel` (string) — both cause `AppDataGrid.FilterFunc`'s reflection lookup (`GetProperty`) to silently return `null` and no-op the filter.

- [ ] **Step 1: Write the failing test**

Add to `AdminMachinesTests.cs`:

```csharp
    [Fact]
    public async Task AdminMachines_Renders_GridSearchBox()
    {
        var cut = RenderWithPopover<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='grid-search-input']");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMachines_Renders_GridSearchBox" --nologo`
Expected: FAIL.

- [ ] **Step 3: Restore `AppDataGrid` and remove the now-redundant explicit pager**

In `AdminMachines.razor`, change:

```razor
    <MudDataGrid T="MachineCatalogRow"
                 Items="@_machines"
                 Groupable="@true"
                 GroupExpanded="@true"
                 RowClick="@(EventCallback.Factory.Create<DataGridRowClickEventArgs<MachineCatalogRow>>(this, OnRowClick))"
                 data-testid="admin-machines-grid">
```

to:

```razor
    <AppDataGrid T="MachineCatalogRow"
                 Items="@_machines"
                 SearchContext="admin-machines"
                 Groupable="@true"
                 GroupExpanded="@true"
                 RowClick="@(EventCallback.Factory.Create<DataGridRowClickEventArgs<MachineCatalogRow>>(this, OnRowClick))"
                 data-testid="admin-machines-grid">
```

Remove the explicit pager block (redundant with `AppDataGrid`'s own default pager):

```razor
        <PagerContent>
            <MudDataGridPager T="MachineCatalogRow" />
        </PagerContent>

```

(delete these 3 lines entirely, including the blank line after).

Change the closing `</MudDataGrid>` (currently right before `</MudContainer>`) to `</AppDataGrid>`.

- [ ] **Step 4: Run the test again to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMachines_Renders_GridSearchBox" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full AdminMachinesTests file to confirm no regressions**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMachinesTests" --nologo`
Expected: all pass, including `AdminMachines_AxisSelector_RendersAllFourAxisButtons` (unaffected — the axis-selector `MudStack` is a sibling of the grid, not touched) and the health-chip tests (unaffected — `CellTemplate`/`GroupTemplate` content inside `<Columns>` is forwarded unchanged via `AppDataGrid`'s `@Columns`).

- [ ] **Step 6: Fix the `admin-machines` section in `Ai/Agents/GridSearch.md`**

Change:

```markdown
### admin-machines
- `Manufacturer` (string)
- `Title` (string)
- `Edition` (string)
- `Year` (int)
- `DocCount` (int)
- `HealthLabel` (string: "OK", "Empty", "No manual", "Edition gap")
- `Franchise` (string)
- `Source` (string)
```

to:

```markdown
### admin-machines
- `Manufacturer` (string)
- `Title` (string)
- `Edition` (string)
- `YearLabel` (string, e.g. "2024" or "Unknown" — numeric comparisons like "gt"/"lt" still work via string-to-number parsing)
- `DocCount` (int)
- `HealthLabel` (string: "OK", "Empty", "No manual", "Edition gap")
- `Source` (string)
```

(`Franchise` removed — the column was deleted from `MachineCatalogRow` in `5870325`, and this repo has no equivalent to reintroduce it. `Year` corrected to `YearLabel` — the actual C# property name; the prior name would have silently no-op'd every year-based filter query.)

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor src/PinballWizard.Application/Ai/Agents/GridSearch.md tests/PinballWizard.Web.Tests/Components/Admin/AdminMachinesTests.cs
git commit -m "fix(web,ai) restore AppDataGrid on AdminMachines; fix stale admin-machines prompt schema"
```

---

### Task 8: Migrate `AdminManufacturers` to `AppDataGrid`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor:63,94-97`
- Modify: `src/PinballWizard.Application/Ai/Agents/GridSearch.md` (add `admin-manufacturers` section)
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminManufacturersTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<ManufacturerRow>` where `ManufacturerRow(string Key, string DisplayName, bool? Enabled, bool HasSource, int Machines)`.
- Produces: `[data-testid='grid-search-input']` now renders on `/admin/manufacturers`; grid honors `Prefs.PageSize` (10) instead of hardcoded 25 (Task 1 already updated the one dependent test for this).

- [ ] **Step 1: Write the failing test**

Add to `AdminManufacturersTests.cs`:

```csharp
    [Fact]
    public async Task Populated_RendersGridSearchBox()
    {
        _machines.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Stream(M("stern", "Stern Pinball")));
        _sources.StreamAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Stream<IngestionSource>());

        var cut = RenderPage();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='grid-search-input']");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminManufacturersTests.Populated_RendersGridSearchBox" --nologo`
Expected: FAIL.

- [ ] **Step 3: Migrate the razor file**

In `AdminManufacturers.razor`, change:

```razor
        <MudDataGrid T="ManufacturerRow" Items="@_rows" RowsPerPage="25" data-testid="manufacturers-table">
```

to:

```razor
        <AppDataGrid T="ManufacturerRow" Items="@_rows" SearchContext="admin-manufacturers" data-testid="manufacturers-table">
```

Remove the explicit pager block:

```razor
            <PagerContent>
                <MudDataGridPager T="ManufacturerRow" />
            </PagerContent>
```

Change the closing `</MudDataGrid>` to `</AppDataGrid>`.

Also update the doc comment on line 24 (`Paged via AppDataGrid (RowsPerPage=25)`) — it was already inaccurate (this page used raw `MudDataGrid`, not `AppDataGrid`, until now) — change to:

```
 * Paged via AppDataGrid (RowsPerPage defaults to the shared 10-row user preference).
```

- [ ] **Step 4: Run the test again to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminManufacturersTests.Populated_RendersGridSearchBox" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full AdminManufacturersTests file**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminManufacturersTests" --nologo`
Expected: all pass, including `PagingAt10_RendersOnlyFirstPageWhenMoreThan10Manufacturers` from Task 1.

- [ ] **Step 6: Add the `admin-manufacturers` section to `Ai/Agents/GridSearch.md`**

Add after the `admin-document-triage` section (before `## Operators`):

```markdown
### admin-manufacturers
- `Key` (string — manufacturer partition key, e.g. "stern")
- `DisplayName` (string)
- `Enabled` (bool — null when no matching ingestion source exists)
- `HasSource` (bool)
- `Machines` (int)
```

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor src/PinballWizard.Application/Ai/Agents/GridSearch.md tests/PinballWizard.Web.Tests/Components/Admin/AdminManufacturersTests.cs
git commit -m "feat(web,ai) migrate AdminManufacturers to AppDataGrid, add admin-manufacturers prompt schema"
```

---

### Task 9: Migrate `AdminSources` to `AppDataGrid`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor:52-55,113-115,117`
- Modify: `src/PinballWizard.Application/Ai/Agents/GridSearch.md` (add `admin-sources` section)
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminSourcesTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<IngestionSourceRow>` where `IngestionSourceRow(string Id, string Name, string SourceUrl, bool Enabled, string Cadence, string LastRun, string LastSuccess, long DocsDiscovered, long RunFailures, string? DiscoveryStatus, string? DiscoveryNotes, DateOnly? DiscoveryDate)`.
- Produces: `[data-testid='grid-search-input']` now renders on `/admin/sources`; grid honors `Prefs.PageSize` (10) instead of hardcoded 25.

- [ ] **Step 1: Write the failing test**

Add to `AdminSourcesTests.cs`:

```csharp
    [Fact]
    public void WithSources_RendersGridSearchBox()
    {
        RegisterSources(ct => Stream([MakeSource("stern", true)], ct));
        _ = Services.GetRequiredService<BunitNavigationManager>();

        var cut = RenderWithPopover<AdminSources>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='grid-search-input']"));
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminSourcesTests.WithSources_RendersGridSearchBox" --nologo`
Expected: FAIL.

- [ ] **Step 3: Migrate the razor file**

In `AdminSources.razor`, change:

```razor
    <MudDataGrid T="IngestionSourceRow"
                 Items="@_sources"
                 RowsPerPage="25"
                 data-testid="admin-sources-grid">
```

to:

```razor
    <AppDataGrid T="IngestionSourceRow"
                 Items="@_sources"
                 SearchContext="admin-sources"
                 data-testid="admin-sources-grid">
```

Remove the explicit pager block:

```razor
        <PagerContent>
            <MudDataGridPager T="IngestionSourceRow" />
        </PagerContent>

```

Change the closing `</MudDataGrid>` to `</AppDataGrid>`.

- [ ] **Step 4: Run the test again to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminSourcesTests.WithSources_RendersGridSearchBox" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full AdminSourcesTests file (both test classes in the file)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminSourcesTests|FullyQualifiedName~AdminSourcesLoadingStateTests" --nologo`
Expected: all pass — the loading-state tests check for the spinner and `mud-progress-indeterminate` class, unaffected by the grid wrapper swap.

- [ ] **Step 6: Add the `admin-sources` section to `Ai/Agents/GridSearch.md`**

Add after the `admin-manufacturers` section:

```markdown
### admin-sources
- `Id` (string)
- `Name` (string)
- `SourceUrl` (string)
- `Enabled` (bool)
- `Cadence` (string)
- `LastRun` (string — formatted date, e.g. "Jul 4, 2026 6:00 PM", or "—" if never run)
- `LastSuccess` (string — same format as LastRun)
- `DocsDiscovered` (int)
- `RunFailures` (int)
```

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor src/PinballWizard.Application/Ai/Agents/GridSearch.md tests/PinballWizard.Web.Tests/Components/Admin/AdminSourcesTests.cs
git commit -m "feat(web,ai) migrate AdminSources to AppDataGrid, add admin-sources prompt schema"
```

---

### Task 10: Migrate `AdminJobDetail` to `AppDataGrid`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobDetail.razor:120-122`
- Modify: `src/PinballWizard.Application/Ai/Agents/GridSearch.md` (add `admin-job-detail` section)
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobDetailTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<JobExecution>` where `JobExecution(string ExecutionName, string Status, DateTimeOffset? StartOn, DateTimeOffset? EndOn)`.
- Produces: `[data-testid='grid-search-input']` now renders on `/admin/jobs/{JobName}`.

- [ ] **Step 1: Write the failing test**

Add to `AdminJobDetailTests.cs` (reusing the existing `RenderPage`/`MakeDetail`/`FlushAsync` helpers):

```csharp
    [Fact]
    public async Task Populated_RendersGridSearchBox()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeDetail()));

        var cut = RenderPage(svc);
        await FlushAsync(cut);

        cut.Find("[data-testid='grid-search-input']");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminJobDetailTests.Populated_RendersGridSearchBox" --nologo`
Expected: FAIL.

- [ ] **Step 3: Migrate the razor file**

In `AdminJobDetail.razor`, change:

```razor
        <MudDataGrid T="JobExecution"
                     Items="@_detail.Executions"
                     data-testid="execution-table">
```

to:

```razor
        <AppDataGrid T="JobExecution"
                     Items="@_detail.Executions"
                     SearchContext="admin-job-detail"
                     data-testid="execution-table">
```

Change the closing `</MudDataGrid>` (right before the `@if (_detail.HasMore)` block) to `</AppDataGrid>`.

Note: this page has no `<PagerContent>` today (it uses a custom "Load more" button for incremental server-side fetching, not client-side MudBlazor paging) — `AppDataGrid`'s default `ShowPager=true` now paginates whatever's already been fetched, alongside the existing "Load more" button that fetches additional rows from the server. These are two independent, non-conflicting mechanisms (client-side paging of loaded rows vs. server-side fetch-more); no `ShowPager="false"` override is needed.

- [ ] **Step 4: Run the test again to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminJobDetailTests.Populated_RendersGridSearchBox" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full AdminJobDetailTests file**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminJobDetailTests" --nologo`
Expected: all pass.

- [ ] **Step 6: Add the `admin-job-detail` section to `Ai/Agents/GridSearch.md`**

Add after the `admin-sources` section:

```markdown
### admin-job-detail
- `ExecutionName` (string)
- `Status` (string)
- `StartOn` (datetime)
- `EndOn` (datetime)
```

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobDetail.razor src/PinballWizard.Application/Ai/Agents/GridSearch.md tests/PinballWizard.Web.Tests/Components/Admin/AdminJobDetailTests.cs
git commit -m "feat(web,ai) migrate AdminJobDetail to AppDataGrid, add admin-job-detail prompt schema"
```

---

### Task 11: Migrate `AdminLinkOverrides` to `AppDataGrid`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor:62-64`
- Modify: `src/PinballWizard.Application/Ai/Agents/GridSearch.md` (add `admin-link-overrides` section)
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminLinkOverridesTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<LinkOverrideRow>` where `LinkOverrideRow(string SourcePattern, string MachineIds, string CreatedBy, string CreatedAt, string? Notes)`.
- Produces: `[data-testid='grid-search-input']` now renders on `/admin/link-overrides`.

- [ ] **Step 1: Write the failing test**

Add to `AdminLinkOverridesTests.cs`:

```csharp
    [Fact]
    public async Task AdminLinkOverrides_Renders_GridSearchBox()
    {
        var cut = RenderWithPopover<AdminLinkOverrides>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='grid-search-input']");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminLinkOverrides_Renders_GridSearchBox" --nologo`
Expected: FAIL.

- [ ] **Step 3: Migrate the razor file**

In `AdminLinkOverrides.razor`, change:

```razor
    <MudDataGrid T="LinkOverrideRow"
                 Items="@_overrides"
                 data-testid="admin-link-overrides-grid">
```

to:

```razor
    <AppDataGrid T="LinkOverrideRow"
                 Items="@_overrides"
                 SearchContext="admin-link-overrides"
                 data-testid="admin-link-overrides-grid">
```

Change the closing `</MudDataGrid>` (right before the create-dialog `@if` block) to `</AppDataGrid>`.

- [ ] **Step 4: Run the test again to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminLinkOverrides_Renders_GridSearchBox" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full AdminLinkOverridesTests file**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminLinkOverridesTests" --nologo`
Expected: all pass.

- [ ] **Step 6: Add the `admin-link-overrides` section to `Ai/Agents/GridSearch.md`**

Add after the `admin-job-detail` section:

```markdown
### admin-link-overrides
- `SourcePattern` (string)
- `MachineIds` (string — comma-joined)
- `CreatedBy` (string)
- `CreatedAt` (string)
- `Notes` (string, nullable)
```

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor src/PinballWizard.Application/Ai/Agents/GridSearch.md tests/PinballWizard.Web.Tests/Components/Admin/AdminLinkOverridesTests.cs
git commit -m "feat(web,ai) migrate AdminLinkOverrides to AppDataGrid, add admin-link-overrides prompt schema"
```

---

### Task 12: Migrate `AdminCorpus` to `AppDataGrid` with search disabled

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminCorpus.razor:65-66,75`
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminCorpusTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<DocTypeChunkCount>` with `EnableAiSearch="false"`.
- Produces: consistent grid styling/pagination on the tiny document-type breakdown table, with NO search box (per the earlier design decision — a ~10-row stats table has nothing meaningful to search, and `AdminCorpus` is static SSR/`[StreamRendering]`, so avoiding the interactive `GridSearch` component here also sidestepped a render-mode mismatch this repo has hit before with interactive islands on static pages).

- [ ] **Step 1: Write the failing test (asserting search is ABSENT, not present)**

Add to `AdminCorpusTests.cs`:

```csharp
    [Fact]
    public async Task Populated_DoesNotRenderGridSearchBox()
    {
        _reader.GetCorpusStatsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(Stats()));

        var cut = RenderCorpus();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Empty(cut.FindAll("[data-testid='grid-search-input']"));
    }
```

- [ ] **Step 2: Run it to verify it currently passes (no grid-search anywhere yet) — this confirms the baseline before the migration, not a red/green cycle**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminCorpusTests.Populated_DoesNotRenderGridSearchBox" --nologo`
Expected: PASS (trivially true today since no grid on this page uses `AppDataGrid` at all yet). This test's value is as a REGRESSION GUARD after Step 3 — confirm it still passes once `AppDataGrid` is wired in with search disabled.

- [ ] **Step 3: Migrate the razor file**

In `AdminCorpus.razor`, change:

```razor
            <MudDataGrid T="DocTypeChunkCount" Items="@_stats.ByDocumentType"
                         data-testid="corpus-table">
```

to:

```razor
            <AppDataGrid T="DocTypeChunkCount" Items="@_stats.ByDocumentType"
                         EnableAiSearch="false"
                         data-testid="corpus-table">
```

Change the closing `</MudDataGrid>` to `</AppDataGrid>`.

- [ ] **Step 4: Run the test again to confirm it still passes (now for the real reason)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminCorpusTests.Populated_DoesNotRenderGridSearchBox" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full AdminCorpusTests file**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminCorpusTests" --nologo`
Expected: all pass, including `Populated_RendersAllThreeSections`, `EmptyIndex_RendersDistinctEmptyState`, `Unreachable_RendersVisibleAlert_NotEmptyState`, `NullFreshness_NonEmpty_ShowsBackfillPending`.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminCorpus.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminCorpusTests.cs
git commit -m "feat(web) migrate AdminCorpus to AppDataGrid with search disabled (stats-only table)"
```

---

### Task 13: `DocumentList.razor` — remove legacy filters, add dual `SearchContext`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Shared/DocumentList.razor:17-58`
- Modify: `src/PinballWizard.Application/Ai/Agents/GridSearch.md` (add `admin-document-list` and `public-document-list` sections)
- Test: `tests/PinballWizard.Web.Tests/Components/Shared/DocumentListTests.cs`

**Interfaces:**
- Consumes: `AppDataGrid<DocumentListItem>` with `SearchContext="@(IsAdmin ? "admin-document-list" : "public-document-list")"`.
- Produces: the "Search by game…" text field and the Manufacturer/Type `MudChipSet` filters are gone from the rendered page; `[data-testid='grid-search-input']` renders in their place. `Game`/`Manufacturer`/`Type` `[Parameter]`s and the `OnParametersSetAsync` → `Repo.StreamDocumentsAsync(...)` call are **unchanged** — they still drive the server-side fetch scope from the URL query string (preserves the `Manufacturers.razor` deep link).

- [ ] **Step 1: Update the existing tests that assert the now-removed UI, and add new ones for the new UI**

In `DocumentListTests.cs`:

Delete this test entirely (it asserts the chip-strip UI, which no longer exists — no behavioral claim survives, since the underlying document-type filtering is still exercised by other, unaffected tests like `TypeQueryParam_PassesTypeFilterToRepository`):

```csharp
    [Fact]
    public async Task TypeFilterChipStrip_RendersUserFacingDocumentTypes()
    {
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([]));

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Strip renders user-facing types and excludes internal artefacts.
        var chipSetMarkup = cut.Find("[data-testid='doc-list-type-filter']").InnerHtml;
        Assert.Contains("Manual", chipSetMarkup);
        Assert.Contains("Rulesheet", chipSetMarkup);
        Assert.DoesNotContain("MetadataCard", chipSetMarkup);
    }
```

Replace `GameQueryParam_InitializesGameFilter` (which currently also asserts the now-removed input element) with a version that keeps only the still-valid behavioral claim (the repo call is forwarded correctly from the query param):

```csharp
    [Fact]
    public async Task GameQueryParam_ForwardsToRepository()
    {
        _repo.StreamDocumentsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([]));

        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<PinballWizard.Web.Components.Shared.DocumentList>(1);
            builder.AddAttribute(2, nameof(PinballWizard.Web.Components.Shared.DocumentList.Game), "Godzilla");
            builder.AddAttribute(3, nameof(PinballWizard.Web.Components.Shared.DocumentList.IsAdmin), false);
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<PinballWizard.Web.Components.Shared.DocumentList>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // The deep-link query param still narrows the server-side fetch — this is
        // the mechanism Manufacturers.razor's /documents?manufacturer=X&game=Y link
        // depends on, and it is deliberately NOT removed by dropping the visible
        // filter UI (see design doc §2).
        _repo.Received(1).StreamDocumentsAsync("Godzilla", Arg.Any<string?>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>());
    }
```

Add new tests asserting the search box replaces the old UI, and that the two `SearchContext` values are distinct:

```csharp
    [Fact]
    public async Task PublicPage_RendersGridSearchBox_NotLegacyFilters()
    {
        _repo.StreamDocumentsAsync(null, null, null, false, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([MakeItem()]));

        var cut = RenderWithPopover<Documents>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='grid-search-input']");
        Assert.Empty(cut.FindAll("[data-testid='doc-list-game-filter']"));
        Assert.Empty(cut.FindAll("[data-testid='doc-list-mfr-filter']"));
        Assert.Empty(cut.FindAll("[data-testid='doc-list-type-filter']"));
    }

    [Fact]
    public async Task AdminPage_RendersGridSearchBox_NotLegacyFilters()
    {
        _repo.StreamDocumentsAsync(null, null, null, true, Arg.Any<CancellationToken>())
             .Returns(_ => FakeStream([MakeItem()]));

        var cut = RenderWithPopover<PinballWizard.Web.Components.Pages.Admin.AdminDocuments>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='grid-search-input']");
        Assert.Empty(cut.FindAll("[data-testid='doc-list-game-filter']"));
    }
```

- [ ] **Step 2: Run the new/updated tests to verify they fail as expected**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~DocumentListTests" --nologo`
Expected: `PublicPage_RendersGridSearchBox_NotLegacyFilters` and `AdminPage_RendersGridSearchBox_NotLegacyFilters` FAIL (no grid-search box yet); `GameQueryParam_ForwardsToRepository` PASSES already (the underlying repo-call behavior is unchanged); the deleted `TypeFilterChipStrip_...` test no longer exists to run.

- [ ] **Step 3: Remove the legacy filter UI and add the dual `SearchContext` in `DocumentList.razor`**

Remove the entire `MudStack` block (the "Search by game…" field and both `MudChipSet`s):

```razor
    <MudStack Row="true" AlignItems="AlignItems.Center" Class="mb-4" Spacing="2">
        <MudTextField T="string"
                      Value="@Game"
                      ValueChanged="@(v => OnGameChanged(v))"
                      Placeholder="Search by game…"
                      Adornment="Adornment.Start"
                      AdornmentIcon="@Icons.Material.Filled.Search"
                      Clearable="true"
                      DebounceInterval="300"
                      Immediate="true"
                      data-testid="doc-list-game-filter" />
        <MudChipSet T="string"
                    SelectedValue="@Manufacturer"
                    SelectedValueChanged="@(v => OnManufacturerChanged(v))"
                    SelectionMode="SelectionMode.SingleSelection"
                    data-testid="doc-list-mfr-filter">
            @foreach (var mfr in _manufacturers)
            {
                <MudChip T="string" Value="@mfr" Variant="Variant.Outlined">@mfr</MudChip>
            }
        </MudChipSet>
        <MudChipSet T="string"
                    SelectedValue="@Type"
                    SelectedValueChanged="@(v => OnTypeChanged(v))"
                    SelectionMode="SelectionMode.SingleSelection"
                    data-testid="doc-list-type-filter">
            @foreach (var dt in _documentTypes)
            {
                <MudChip T="string" Value="@dt" Variant="Variant.Outlined" aria-label="@($"Filter by {dt}")">@dt</MudChip>
            }
        </MudChipSet>
    </MudStack>

```

(delete entirely, including the trailing blank line).

Change the `AppDataGrid` call site from:

```razor
    <AppDataGrid T="DocumentListItem"
                 Items="@_documents"
                 data-testid="doc-list-grid"
                 RowClick="@(EventCallback.Factory.Create<DataGridRowClickEventArgs<DocumentListItem>>(this, OnRowClick))">
```

to:

```razor
    <AppDataGrid T="DocumentListItem"
                 Items="@_documents"
                 SearchContext="@(IsAdmin ? "admin-document-list" : "public-document-list")"
                 data-testid="doc-list-grid"
                 RowClick="@(EventCallback.Factory.Create<DataGridRowClickEventArgs<DocumentListItem>>(this, OnRowClick))">
```

Do **not** remove `Game`, `Manufacturer`, `Type` parameters, `OnParametersSetAsync`, `OnGameChanged`/`OnManufacturerChanged`/`OnTypeChanged`, or the `_manufacturers`/`_documentTypes` arrays from the `@code` block — `OnParametersSetAsync` still calls `Repo.StreamDocumentsAsync(Game, Manufacturer, Type, IsAdmin, token)` using these parameters, which is the mechanism that keeps the `Manufacturers.razor` deep link working. Only the visible `MudStack` filter UI is removed; the change-handler methods (`OnGameChanged`, etc.) become dead code with no remaining caller — remove them along with `_manufacturers` and `_documentTypes` (nothing else references them once the chip UI is gone):

```csharp
    private void OnGameChanged(string? value)
    {
        var uri = Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["game"] = string.IsNullOrWhiteSpace(value) ? null : value,
            ["manufacturer"] = Manufacturer,
            ["type"] = Type
        });
        Nav.NavigateTo(uri, replace: true);
    }

    private void OnManufacturerChanged(string? value)
    {
        var uri = Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["game"] = Game,
            ["manufacturer"] = string.IsNullOrWhiteSpace(value) ? null : value,
            ["type"] = Type
        });
        Nav.NavigateTo(uri, replace: true);
    }

    private void OnTypeChanged(string? value)
    {
        var uri = Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["game"] = Game,
            ["manufacturer"] = Manufacturer,
            ["type"] = string.IsNullOrWhiteSpace(value) ? null : value
        });
        Nav.NavigateTo(uri, replace: true);
    }
```

remove these 3 methods entirely, and remove:

```csharp
    private static readonly string[] _manufacturers =
    [
        "American Pinball", "Barrels of Fun", "Chicago Gaming",
        "Jersey Jack", "Multimorphic", "Pinball Brothers",
        "Spooky", "Stern"
    ];

    // Valid document_type values derived from PinballWizard.Core.Models.DocumentType enum.
    // Synthesized types (MetadataCard, GameOverview, NewsDigest) are omitted: they are
    // internal catalog artefacts not surfaced as user-selectable filter options.
    private static readonly string[] _documentTypes =
    [
        "Manual", "Schematic", "Firmware", "ServiceBulletin",
        "Flyer", "SpecSheet", "FeatureMatrix", "Rulesheet",
        "Readme", "Other"
    ];
```

`Nav` (the injected `NavigationManager`) has no other caller after these removals — check the rest of the file; if `Nav` is otherwise unused, remove `@inject NavigationManager Nav` too. (It is not used elsewhere in this file — the `DocUrl`/row-click navigation uses `Nav.NavigateTo` in `OnRowClick`, so **keep** `@inject NavigationManager Nav`; only the 3 filter-change handlers above are removed.)

- [ ] **Step 4: Run the tests again to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~DocumentListTests" --nologo`
Expected: all pass.

- [ ] **Step 5: Run the full DocumentListTests file for a final confirmation**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~DocumentListTests" --nologo`
Expected: `Passed! - Failed: 0` — including `ShowsDocumentsFromRepository`, `ManufacturerCell_LinksToManufacturerDetailPage`, `ManufacturerCell_NullKey_DegradesToTextWithNoLink`, `EmptyCorpus_ShowsEmptyState`, `WithFilters_NoResults_ShowsFilteredEmptyState`, `AdminColumns_HiddenOnPublicPage`, `AdminPage_ShowsAdminColumns`, `RepositoryError_ShowsErrorAlert`, `TypeQueryParam_PassesTypeFilterToRepository`, `ManufacturerQueryParam_PassesManufacturerFilterToRepository`, `TypeFilter_WithNoResults_ShowsFilteredEmptyState` (all unaffected by the UI removal — they exercise the grid/query-param/repo-call behavior, not the removed chip/text-box elements).

- [ ] **Step 6: Add both document-list sections to `Ai/Agents/GridSearch.md`**

Add after the `admin-link-overrides` section:

```markdown
### admin-document-list
- `Title` (string)
- `DocumentType` (string)
- `GameTitle` (string, nullable)
- `Edition` (string, nullable)
- `Manufacturer` (string)
- `FileFormat` (string)
- `PageCount` (int, nullable)
- `SizeBytes` (int, nullable)
- `FirstDiscoveredAt` (datetime)
- `LinkStatus` (string, nullable — admin-only)
- `LinkFailureReason` (string, nullable — admin-only)
- `ResolutionStrategy` (string, nullable — admin-only)

### public-document-list
Same as `admin-document-list` but WITHOUT `LinkStatus`, `LinkFailureReason`, or
`ResolutionStrategy` — those fields are always null on the public projection, so a
query like "failed links" would otherwise silently match nothing. Use this context
for a public (non-admin) visitor's query.
- `Title` (string)
- `DocumentType` (string)
- `GameTitle` (string, nullable)
- `Edition` (string, nullable)
- `Manufacturer` (string)
- `FileFormat` (string)
- `PageCount` (int, nullable)
- `SizeBytes` (int, nullable)
- `FirstDiscoveredAt` (datetime)
```

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Shared/DocumentList.razor src/PinballWizard.Application/Ai/Agents/GridSearch.md tests/PinballWizard.Web.Tests/Components/Shared/DocumentListTests.cs
git commit -m "feat(web,ai) replace DocumentList legacy filters with GridSearch; add dual document-list prompt schemas"
```

---

## Final verification (after all 13 tasks)

- [ ] Run the full repo test suite: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E" --nologo`
  Expected: all pass, zero regressions.
- [ ] Run a zero-warning build: `dotnet build PinballWizard.slnx --nologo -warnaserror`
  Expected: 0 warnings, 0 errors.
- [ ] Manually spot-check in a running instance (per this repo's `/verify` convention): navigate to `/admin/machines`, `/admin/jobs`, `/admin/document-triage`, `/admin/manufacturers`, `/admin/sources`, `/admin/jobs/{name}`, `/admin/link-overrides`, `/admin/corpus`, `/admin/documents`, `/documents` — confirm the search box appears everywhere except `/admin/corpus`, confirm typing a query on `/admin/machines` (e.g. "Stern machines") actually filters, confirm a semantic-style query (e.g. a franchise name that appears in a Title) narrows results, confirm every grid shows 10 rows/page by default.
