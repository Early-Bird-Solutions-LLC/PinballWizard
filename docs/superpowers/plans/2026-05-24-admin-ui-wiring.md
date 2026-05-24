# Admin UI Wiring — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire `IRawDocumentRepository` + `IDocumentLinker` into `AdminDocumentTriage.razor` so the grid loads real `Failed/NotInCatalog/PlatformGeneric` documents; wire `ILinkOverrideRepository` into `AdminLinkOverrides.razor` so the grid loads real overrides with create/delete actions.

**Architecture:** Both admin pages are Blazor Server components under `AdminLayout`. They use `@inject` for Application-layer interfaces already registered in the Web project's DI container by `AddCosmosPersistence`. MudBlazor (ADR-0008) is the only UI library — `MudDataGrid`, `MudDialog`, `MudSnackbar`, `MudProgressCircular`. No new services, interfaces, or infrastructure are introduced; this is wiring-only work.

**Tech Stack:** Blazor Server (.NET 10), MudBlazor 8.x, `IRawDocumentRepository`, `IDocumentLinker`, `ILinkOverrideRepository`, `LinkOverrideRecord`, `RawDocumentRecord`, `LinkStatus`

---

## File Map

| File | Change |
|---|---|
| `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor` | Replace skeleton with real `IRawDocumentRepository` + `IDocumentLinker` wiring; add re-link and mark-generic actions |
| `src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor` | Replace skeleton with real `ILinkOverrideRepository` wiring; add create-override dialog and delete action |

---

### Task 1: Create feature branch

- [ ] **Step 1: Create branch**

```bash
git checkout main && git pull
git checkout -b feature/admin-ui-wiring
```

- [ ] **Step 2: Confirm starting commit**

```bash
git log --oneline -1
```

---

### Task 2: Wire `AdminDocumentTriage.razor`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor`

Key facts about the interfaces:

`IRawDocumentRepository.StreamByStatusAsync(IReadOnlyCollection<LinkStatus> statuses, CancellationToken)` returns `IAsyncEnumerable<RawDocumentRecord>`.

`RawDocumentRecord` has:
- `string DocumentId`
- `LinkStatus LinkStatus`
- `string? FailureReason`
- `SourceInfo Source` with `.DiscoveryUrl`, `.LinkText`
- `ClassificationInfo Classification` with `.DocumentType` (a `DocumentType` enum)
- `TimelineInfo Timeline` with `.LastCheckedAt` (a `DateTime`)

`IDocumentLinker.LinkAsync(RawDocumentRecord raw, CancellationToken)` returns `Task<LinkingResult>`.
`IDocumentLinker.InitializeAsync(CancellationToken)` must be called before `LinkAsync`.

`IRawDocumentRepository.UpdateLinkStatusAsync(string documentId, LinkStatus status, string? resolutionStrategy, string? failureReason, string? overrideId, CancellationToken)` returns `Task`.

`LinkStatus` enum values include `Failed`, `NotInCatalog`, `PlatformGeneric`, `Linked`, `ManuallyLinked`, `Pending`.

- [ ] **Step 1: Replace the entire file content**

```razor
@page "/admin/document-triage"
@layout AdminLayout

@* AdminDocumentTriage — /admin/document-triage unlinked/failed document triage view.
 *
 * Renders a MudDataGrid of documents with LinkStatus in {Failed, NotInCatalog, PlatformGeneric}.
 * Capped at 200 rows (triage backlog guard). Actions: Re-link (runs IDocumentLinker.LinkAsync
 * for the single record), Mark Generic (stamps PlatformGeneric via UpdateLinkStatusAsync).
 *
 * ADR-0008  — MudBlazor strict
 * ADR-0009  — Entra External ID auth
 * ADR-0026  § 1 — routing inventory (/admin/document-triage)
 *@

@using PinballWizard.Application.Linking
@using PinballWizard.Application.Persistence
@using PinballWizard.Core.Models

@inject IRawDocumentRepository RawDocRepo
@inject IDocumentLinker DocumentLinker
@inject ISnackbar Snackbar

<PageTitle>Document Triage — PinballWizard Admin</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="py-6">
    <MudBreadcrumbs Items="_breadcrumbs" Class="pa-0 mb-4" />

    <MudText Typo="Typo.h4" GutterBottom="true">Document Triage</MudText>
    <MudText Typo="Typo.body2" Color="Color.Secondary" Class="mb-6">
        Unlinked, failed, and platform-generic documents awaiting resolution.
    </MudText>

    @if (_loading)
    {
        <MudProgressLinear Indeterminate="true" Color="Color.Primary" Class="mb-4" />
    }

    <MudDataGrid T="DocumentTriageRow"
                 Items="@_documents"
                 Hover="true"
                 Striped="true"
                 Dense="true"
                 Elevation="2"
                 data-testid="admin-document-triage-grid">

        <Columns>
            <PropertyColumn Property="x => x.DocumentType" Title="Document Type" />
            <PropertyColumn Property="x => x.SourceUrl" Title="Source URL" />
            <PropertyColumn Property="x => x.LinkText" Title="Link Text" />
            <TemplateColumn Title="Status">
                <CellTemplate>
                    <MudChip T="string"
                             Size="Size.Small"
                             Color="@StatusColor(context.Item.Status)"
                             Variant="Variant.Filled">
                        @context.Item.Status
                    </MudChip>
                </CellTemplate>
            </TemplateColumn>
            <PropertyColumn Property="x => x.FailureReason" Title="Failure Reason" />
            <PropertyColumn Property="x => x.LastAttemptedAt" Title="Last Attempt" />
            <TemplateColumn Title="Actions">
                <CellTemplate>
                    @if (context.Item.ActionBusy)
                    {
                        <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                    }
                    else
                    {
                        <MudStack Row="true" Spacing="1">
                            <MudButton Size="Size.Small"
                                       Variant="Variant.Text"
                                       Color="Color.Primary"
                                       OnClick="@(() => RelinkAsync(context.Item))">
                                Re-link
                            </MudButton>
                            <MudButton Size="Size.Small"
                                       Variant="Variant.Text"
                                       Color="Color.Secondary"
                                       OnClick="@(() => MarkGenericAsync(context.Item))">
                                Mark Generic
                            </MudButton>
                        </MudStack>
                    }
                </CellTemplate>
            </TemplateColumn>
        </Columns>

        <NoRecordsContent>
            <MudStack AlignItems="AlignItems.Center" Class="py-8" Spacing="2">
                <MudIcon Icon="@Icons.Material.Outlined.CheckCircle"
                         Size="Size.Large"
                         Color="Color.Tertiary" />
                <MudText Typo="Typo.body1" data-testid="admin-document-triage-empty">
                    No documents awaiting triage.
                </MudText>
                <MudText Typo="Typo.body2" Color="Color.Secondary">
                    All scraped documents have been linked or resolved.
                </MudText>
            </MudStack>
        </NoRecordsContent>

    </MudDataGrid>
</MudContainer>

@code {
    private sealed class DocumentTriageRow
    {
        public required string DocumentId { get; init; }
        public required string DocumentType { get; init; }
        public required string SourceUrl { get; init; }
        public required string LinkText { get; init; }
        public required string Status { get; init; }
        public required string FailureReason { get; init; }
        public required string LastAttemptedAt { get; init; }
        public bool ActionBusy { get; set; }
    }

    private List<DocumentTriageRow> _documents = [];
    private bool _loading = true;

    private readonly List<BreadcrumbItem> _breadcrumbs =
    [
        new BreadcrumbItem("Admin", href: "/admin", icon: Icons.Material.Filled.Dashboard),
        new BreadcrumbItem("Document Triage", href: "/admin/document-triage", icon: Icons.Material.Filled.RuleFolder),
    ];

    protected override async Task OnInitializedAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var statuses = new[] { LinkStatus.Failed, LinkStatus.NotInCatalog, LinkStatus.PlatformGeneric };
            var rows = new List<DocumentTriageRow>();
            await foreach (var doc in RawDocRepo.StreamByStatusAsync(statuses, cts.Token))
            {
                rows.Add(new DocumentTriageRow
                {
                    DocumentId = doc.DocumentId,
                    DocumentType = doc.Classification.DocumentType.ToString(),
                    SourceUrl = doc.Source.DiscoveryUrl,
                    LinkText = doc.Source.LinkText ?? string.Empty,
                    Status = doc.LinkStatus.ToString(),
                    FailureReason = doc.FailureReason ?? string.Empty,
                    LastAttemptedAt = doc.Timeline.LastCheckedAt.ToString("u")
                });
                if (rows.Count >= 200) break;
            }
            _documents = rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Snackbar.Add($"Failed to load triage queue: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RelinkAsync(DocumentTriageRow row)
    {
        row.ActionBusy = true;
        StateHasChanged();
        try
        {
            await DocumentLinker.InitializeAsync(CancellationToken.None);
            var rawDoc = await RawDocRepo.GetAsync(row.DocumentId, CancellationToken.None);
            if (rawDoc is null)
            {
                Snackbar.Add($"Document {row.DocumentId} not found.", Severity.Warning);
                return;
            }
            var result = await DocumentLinker.LinkAsync(rawDoc, CancellationToken.None);
            if (result.FinalStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked)
            {
                _documents.Remove(row);
                Snackbar.Add($"Linked to {result.LinkedMachineIds.Count} machine(s) via {result.ResolutionStrategy}.", Severity.Success);
            }
            else
            {
                row = row with { Status = result.FinalStatus.ToString(), FailureReason = result.FailureReason ?? string.Empty };
                var idx = _documents.FindIndex(d => d.DocumentId == row.DocumentId);
                if (idx >= 0) _documents[idx] = row;
                Snackbar.Add($"Still unlinked: {result.FinalStatus} — {result.FailureReason}", Severity.Warning);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Re-link failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            row.ActionBusy = false;
            StateHasChanged();
        }
    }

    private async Task MarkGenericAsync(DocumentTriageRow row)
    {
        row.ActionBusy = true;
        StateHasChanged();
        try
        {
            await RawDocRepo.UpdateLinkStatusAsync(
                row.DocumentId,
                LinkStatus.PlatformGeneric,
                resolutionStrategy: "admin_manual",
                failureReason: null,
                overrideId: null,
                CancellationToken.None);
            _documents.Remove(row);
            Snackbar.Add("Marked as platform-generic.", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Mark Generic failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            row.ActionBusy = false;
            StateHasChanged();
        }
    }

    private static Color StatusColor(string status) => status switch
    {
        "Failed"          => Color.Error,
        "NotInCatalog"    => Color.Warning,
        "PlatformGeneric" => Color.Info,
        _                 => Color.Default,
    };
}
```

Note: `DocumentTriageRow` is changed from a `record` to a `class` with a mutable `ActionBusy` property so the spinner state can be toggled in place. The `with` expression is used in `RelinkAsync` to update the immutable fields into a new row object.

- [ ] **Step 2: Build the Web project**

```bash
dotnet build src/PinballWizard.Web/PinballWizard.Web.csproj
```

Expected: 0 errors, 0 warnings.

---

### Task 3: Wire `AdminLinkOverrides.razor`

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor`

Key facts about the interfaces:

`ILinkOverrideRepository.LoadAllAsync(CancellationToken)` returns `Task<IReadOnlyDictionary<string, LinkOverrideRecord>>`.
- Call `.Values` on the result to get `IEnumerable<LinkOverrideRecord>`.

`LinkOverrideRecord` has:
- `string SourcePattern` — the partition key + document id
- `string[] MachineIds` — join with `", "` for display
- `string CreatedBy`
- `DateTimeOffset CreatedAt`
- `string? Notes`

`ILinkOverrideRepository.UpsertAsync(LinkOverrideRecord record, CancellationToken)` returns `Task`.
`ILinkOverrideRepository.DeleteAsync(string sourcePattern, CancellationToken)` returns `Task`.
`ILinkOverrideRepository.GetAsync(string sourcePattern, CancellationToken)` returns `Task<LinkOverrideRecord?>`.

- [ ] **Step 1: Replace the entire file content**

```razor
@page "/admin/link-overrides"
@layout AdminLayout

@* AdminLinkOverrides — /admin/link-overrides admin-configured document-to-machine mapping view.
 *
 * Renders a MudDataGrid of link overrides loaded from ILinkOverrideRepository.LoadAllAsync.
 * Actions: New Override (create dialog), Delete (confirm dialog per row).
 *
 * ADR-0008  — MudBlazor strict
 * ADR-0009  — Entra External ID auth
 * ADR-0026  § 1 — routing inventory (/admin/link-overrides)
 *@

@using PinballWizard.Application.Persistence
@using PinballWizard.Core.Models

@inject ILinkOverrideRepository OverrideRepo
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<PageTitle>Link Overrides — PinballWizard Admin</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="py-6">
    <MudBreadcrumbs Items="_breadcrumbs" Class="pa-0 mb-4" />

    <MudStack Row="true" AlignItems="AlignItems.Center" Class="mb-6">
        <MudText Typo="Typo.h4" Style="flex:1">Link Overrides</MudText>
        <MudButton Variant="Variant.Filled"
                   Color="Color.Primary"
                   StartIcon="@Icons.Material.Filled.Add"
                   OnClick="OpenCreateDialogAsync">
            New Override
        </MudButton>
    </MudStack>

    <MudText Typo="Typo.body2" Color="Color.Secondary" Class="mb-4">
        Admin-configured document-to-machine mappings.
    </MudText>

    @if (_loading)
    {
        <MudProgressLinear Indeterminate="true" Color="Color.Primary" Class="mb-4" />
    }

    <MudDataGrid T="LinkOverrideRow"
                 Items="@_overrides"
                 Hover="true"
                 Striped="true"
                 Dense="true"
                 Elevation="2"
                 data-testid="admin-link-overrides-grid">

        <Columns>
            <PropertyColumn Property="x => x.SourcePattern" Title="Source Pattern" />
            <PropertyColumn Property="x => x.MachineIds" Title="Machine IDs" />
            <PropertyColumn Property="x => x.CreatedBy" Title="Created By" />
            <PropertyColumn Property="x => x.CreatedAt" Title="Created At" />
            <PropertyColumn Property="x => x.Notes" Title="Notes" />
            <TemplateColumn Title="Actions">
                <CellTemplate>
                    <MudButton Size="Size.Small"
                               Variant="Variant.Text"
                               Color="Color.Error"
                               OnClick="@(() => DeleteAsync(context.Item))">
                        Delete
                    </MudButton>
                </CellTemplate>
            </TemplateColumn>
        </Columns>

        <NoRecordsContent>
            <MudStack AlignItems="AlignItems.Center" Class="py-8" Spacing="2">
                <MudIcon Icon="@Icons.Material.Outlined.Inbox"
                         Size="Size.Large"
                         Color="Color.Tertiary" />
                <MudText Typo="Typo.body1" data-testid="admin-link-overrides-empty">
                    No overrides configured.
                </MudText>
                <MudText Typo="Typo.body2" Color="Color.Secondary">
                    Overrides are created manually to resolve ambiguous or failed document links.
                </MudText>
            </MudStack>
        </NoRecordsContent>

    </MudDataGrid>
</MudContainer>

@* Create Override dialog *@
<MudDialog @ref="_createDialog">
    <TitleContent>New Link Override</TitleContent>
    <DialogContent>
        <MudStack Spacing="3">
            <MudTextField @bind-Value="_newPattern"
                          Label="Source Pattern"
                          HelperText="Format: discovery_url|DocumentType (e.g. https://sternpinball.com/game/foo/|Manual)"
                          Required="true" />
            <MudTextField @bind-Value="_newMachineIds"
                          Label="Machine IDs"
                          HelperText="Comma-separated OPDB machine IDs. Leave empty to mark as platform-generic." />
            <MudTextField @bind-Value="_newNotes"
                          Label="Notes"
                          Lines="2" />
        </MudStack>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="@(() => _createDialog?.Close())">Cancel</MudButton>
        <MudButton Color="Color.Primary"
                   Variant="Variant.Filled"
                   OnClick="ConfirmCreateAsync"
                   Disabled="@string.IsNullOrWhiteSpace(_newPattern)">
            Create
        </MudButton>
    </DialogActions>
</MudDialog>

@code {
    private sealed record LinkOverrideRow(
        string SourcePattern,
        string MachineIds,
        string CreatedBy,
        string CreatedAt,
        string? Notes);

    private List<LinkOverrideRow> _overrides = [];
    private bool _loading = true;
    private MudDialog? _createDialog;
    private string _newPattern = string.Empty;
    private string _newMachineIds = string.Empty;
    private string _newNotes = string.Empty;

    private readonly List<BreadcrumbItem> _breadcrumbs =
    [
        new BreadcrumbItem("Admin", href: "/admin", icon: Icons.Material.Filled.Dashboard),
        new BreadcrumbItem("Link Overrides", href: "/admin/link-overrides", icon: Icons.Material.Filled.LinkOff),
    ];

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var all = await OverrideRepo.LoadAllAsync(CancellationToken.None);
            _overrides = all.Values
                .Select(r => new LinkOverrideRow(
                    SourcePattern: r.SourcePattern,
                    MachineIds: string.Join(", ", r.MachineIds),
                    CreatedBy: r.CreatedBy,
                    CreatedAt: r.CreatedAt.ToString("u"),
                    Notes: r.Notes))
                .ToList();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load overrides: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private void OpenCreateDialogAsync()
    {
        _newPattern = string.Empty;
        _newMachineIds = string.Empty;
        _newNotes = string.Empty;
        _createDialog?.Show();
    }

    private async Task ConfirmCreateAsync()
    {
        var machineIds = _newMachineIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var record = new LinkOverrideRecord
        {
            SourcePattern = _newPattern.Trim(),
            MachineIds = machineIds,
            CreatedBy = "admin",
            CreatedAt = DateTimeOffset.UtcNow,
            Notes = string.IsNullOrWhiteSpace(_newNotes) ? null : _newNotes.Trim()
        };

        try
        {
            await OverrideRepo.UpsertAsync(record, CancellationToken.None);
            _overrides.Add(new LinkOverrideRow(
                SourcePattern: record.SourcePattern,
                MachineIds: string.Join(", ", record.MachineIds),
                CreatedBy: record.CreatedBy,
                CreatedAt: record.CreatedAt.ToString("u"),
                Notes: record.Notes));
            _createDialog?.Close();
            Snackbar.Add("Override created.", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Create failed: {ex.Message}", Severity.Error);
        }
    }

    private async Task DeleteAsync(LinkOverrideRow row)
    {
        var confirmed = await DialogService.ShowMessageBox(
            "Delete Override",
            $"Remove override for '{row.SourcePattern}'?",
            yesText: "Delete",
            cancelText: "Cancel");

        if (confirmed != true) return;

        try
        {
            await OverrideRepo.DeleteAsync(row.SourcePattern, CancellationToken.None);
            _overrides.Remove(row);
            Snackbar.Add("Override deleted.", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Delete failed: {ex.Message}", Severity.Error);
        }
    }
}
```

- [ ] **Step 2: Build the Web project**

```bash
dotnet build src/PinballWizard.Web/PinballWizard.Web.csproj
```

Expected: 0 errors, 0 warnings.

---

### Task 4: Run the Web test suite

**Files:**
- (no file changes — verify only)

- [ ] **Step 1: Run Web.Tests**

```bash
dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj
```

Expected: all 308 tests pass. No test assertions should have been broken — the test suite tests the page routes and layout, not the skeleton data.

- [ ] **Step 2: If any test references the old skeleton field `_documents = []` or `_overrides = []` as a list type**

Update the test to use the new `List<DocumentTriageRow>` field type. The admin page tests likely check for route existence and rendered page title, not the grid data, so failures here would be surprising.

---

### Task 5: Manual smoke test (local Aspire dev)

- [ ] **Step 1: Start the AppHost**

```powershell
pwsh ./start-apphost.ps1
```

Wait for Cosmos emulator and Azurite to appear healthy in the Aspire dashboard.

- [ ] **Step 2: Run the Web project under Aspire**

Navigate to `https://localhost:XXXX/admin/document-triage` in a browser (check Aspire dashboard for port).

- [ ] **Step 3: Verify triage page loads**

Expected:
- Page loads without error
- If `scraped_documents_raw` contains records with `Failed/NotInCatalog/PlatformGeneric` status, they appear in the grid
- If empty, the "No documents awaiting triage" empty state renders
- Loading spinner appears briefly during the 30-second timeout window

- [ ] **Step 4: Verify overrides page loads**

Navigate to `/admin/link-overrides`.

Expected:
- Page loads without error
- If `link_overrides` Cosmos container has records, they appear
- "New Override" button is visible
- Clicking "New Override" opens the dialog
- Dialog fields accept input; "Create" button is disabled when pattern is empty

---

### Task 6: Commit

- [ ] **Step 1: Stage changed files**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor
git add src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor
```

- [ ] **Step 2: Commit**

```bash
git commit -m "feat(admin) AB#259: wire IRawDocumentRepository + IDocumentLinker into AdminDocumentTriage; wire ILinkOverrideRepository into AdminLinkOverrides"
```

---

## Self-Review

**Spec coverage:**
- `StreamByStatusAsync([Failed, NotInCatalog, PlatformGeneric])` in `OnInitializedAsync` ✓ (Task 2 Step 1)
- `Take(200)` safety cap ✓ (Task 2 Step 1 — `if (rows.Count >= 200) break;`)
- `DocumentTriageRow` fields match `RawDocumentRecord` shape ✓ (Task 2 Step 1)
- Re-link button calls `IDocumentLinker.LinkAsync` + `InitializeAsync` ✓ (Task 2 Step 1 `RelinkAsync`)
- Mark-Generic calls `UpdateLinkStatusAsync(PlatformGeneric, ...)` ✓ (Task 2 Step 1 `MarkGenericAsync`)
- In-cell spinner (`ActionBusy`) during async calls ✓ (Task 2 Step 1)
- Errors surface as `MudSnackbar` ✓ (Task 2 Step 1)
- `LoadAllAsync` returns dict → call `.Values` ✓ (Task 3 Step 1 `OnInitializedAsync`)
- `LinkOverrideRow` fields match `LinkOverrideRecord` ✓ (Task 3 Step 1)
- "New Override" toolbar button with `MudDialog` ✓ (Task 3 Step 1)
- Delete action with confirm dialog ✓ (Task 3 Step 1 `DeleteAsync`)
- No edit-in-place (create + delete only) ✓ (in scope guard — no edit)
- No pagination — `Take(200)` cap only ✓

**No placeholders:** all Razor snippets are complete and literal.

**Type consistency:**
- `IRawDocumentRepository.StreamByStatusAsync` takes `IReadOnlyCollection<LinkStatus>` — the `new[] { LinkStatus.Failed, ... }` array satisfies the covariance.
- `IDocumentLinker.LinkAsync` takes `RawDocumentRecord` — fetched via `GetAsync` before calling.
- `ILinkOverrideRepository.UpsertAsync` takes `LinkOverrideRecord` — constructed with `required` properties set.
- `ILinkOverrideRepository.DeleteAsync` takes `string sourcePattern` — passed as `row.SourcePattern`.
- `LinkOverrideRecord.MachineIds` is `string[]` — constructed from `_newMachineIds.Split(',', ...)`.

**MudBlazor compatibility (ADR-0008):** `MudDialog` referenced via `@ref` and `.Show()/.Close()` is the standard MudBlazor 8.x instance pattern. `IDialogService.ShowMessageBox` is used for the delete confirm — both are standard MudBlazor APIs.
