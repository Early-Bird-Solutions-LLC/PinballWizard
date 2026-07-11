# PR 1 — Data-Surface Consistency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every admin list page and the dashboard obey one closed color palette, drop two zero-value columns, and comma-format all counts.

**Architecture:** Collapse four divergent private `LinkStatusColor()` copies into one shared `DocumentLinkStatusColor` helper; recolor the three centralized status helpers to the closed 5-role palette; decouple `AppSummaryCard`'s CTA color from its icon color; remove the Documents "Format" column and fold Triage's "Document ID" into the Link Text cell; add `N0` formatting to counts. A guard test locks the palette against regression.

**Tech Stack:** .NET 10, Blazor Web App, MudBlazor 9 (strict — no raw HTML chrome), bUnit + xUnit, `PinballWizard.Web.Tests`.

**Spec:** `docs/superpowers/specs/2026-07-07-admin-consistency-design.md` (§4).

## Global Constraints

- **Closed color palette (§4.1):** status badges use only `Color.Success` (green = success/healthy/active/OK), `Color.Error` (red = failure/missing/refused/not-in-catalog), or `Color.Default` (neutral = informational/unknown/suppressed/non-status tag). `Color.Primary` (amber) is **interactive-only** (links, buttons, active nav, CTAs) and is **never** a status color. `Color.Info` (blue), `Color.Warning` (amber), and `Color.Tertiary` (teal) are **banned** as status colors.
- **No hex colors** — MudBlazor theme tokens only (ADR-0008 / FE-08).
- **MudBlazor strict** — no raw `<table>`/`<button>`; `MudTable`/`MudSimpleTable` banned in the page layer.
- **Commits:** author `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; conventional type `fix(ui)`/`feat(ui)`; **no** Claude attribution trailer.
- **Before push:** full CI-equivalent suite — `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.
- **Execute in an isolated worktree** off current `main`: `git worktree add .worktrees/pr1-data-surface -b fix/admin-data-surface-consistency`.

---

### Task 1: Shared `DocumentLinkStatusColor` helper

Replaces four divergent private `LinkStatusColor()`/`StatusColor()` copies with one source of truth, correcting `NotInCatalog`→red, `PlatformGeneric`/`platform_generic`→neutral (was amber/blue).

**Files:**
- Create: `src/PinballWizard.Web/Components/Shared/DocumentLinkStatusColor.cs`
- Create: `tests/PinballWizard.Web.Tests/Components/Shared/DocumentLinkStatusColorTests.cs`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor:269-275`
- Modify: `src/PinballWizard.Web/Components/Shared/DocumentList.razor:165-171`
- Modify: `src/PinballWizard.Web/Components/Shared/MachineDetail.razor:347-354`
- Modify: `src/PinballWizard.Web/Components/Shared/DocumentDetail.razor:242-248`

**Interfaces:**
- Produces: `internal static Color DocumentLinkStatusColor.For(string? status)` in namespace `PinballWizard.Web.Components.Shared`.

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Web.Tests/Components/Shared/DocumentLinkStatusColorTests.cs`:

```csharp
using MudBlazor;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class DocumentLinkStatusColorTests
{
    [Theory]
    // success family — both casings
    [InlineData("linked", "Success")]
    [InlineData("Linked", "Success")]
    [InlineData("manually_linked", "Success")]
    [InlineData("ManuallyLinked", "Success")]
    // failure family — includes not-in-catalog (was amber on triage → must be red)
    [InlineData("failed", "Error")]
    [InlineData("Failed", "Error")]
    [InlineData("not_in_catalog", "Error")]
    [InlineData("NotInCatalog", "Error")]
    // non-status tag — platform_generic must be neutral (was amber/blue)
    [InlineData("platform_generic", "Default")]
    [InlineData("PlatformGeneric", "Default")]
    // unknown / null → neutral
    [InlineData("something_else", "Default")]
    [InlineData(null, "Default")]
    public void For_MapsToClosedPalette(string? status, string expectedColor)
    {
        Assert.Equal(expectedColor, DocumentLinkStatusColor.For(status).ToString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~DocumentLinkStatusColorTests"`
Expected: FAIL — build error, `DocumentLinkStatusColor` does not exist.

- [ ] **Step 3: Create the helper**

Create `src/PinballWizard.Web/Components/Shared/DocumentLinkStatusColor.cs`:

```csharp
using MudBlazor;

namespace PinballWizard.Web.Components.Shared;

/// Single source of truth for document link-status → MudBlazor Color.
/// Handles both the PascalCase enum form (LinkStatus.ToString(), e.g. "NotInCatalog")
/// and the snake_case Cosmos-stored form (e.g. "not_in_catalog").
/// Enforces the closed 5-role palette
/// (docs/superpowers/specs/2026-07-07-admin-consistency-design.md §4.1):
/// amber is interactive-only and is never a status color.
internal static class DocumentLinkStatusColor
{
    internal static Color For(string? status) => status switch
    {
        "linked" or "Linked"
            or "manually_linked" or "ManuallyLinked" => Color.Success,
        "failed" or "Failed"
            or "not_in_catalog" or "NotInCatalog"    => Color.Error,
        "platform_generic" or "PlatformGeneric"      => Color.Default, // non-status tag → neutral
        _                                            => Color.Default,
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~DocumentLinkStatusColorTests"`
Expected: PASS (14 cases).

- [ ] **Step 5: Delegate all four call sites to the helper**

In `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor`, replace lines 269-275:

```csharp
    private static Color StatusColor(string status) => status switch
    {
        "Failed"          => Color.Error,
        "NotInCatalog"    => Color.Warning,
        "PlatformGeneric" => Color.Info,
        _                 => Color.Default,
    };
```

with:

```csharp
    private static Color StatusColor(string status) =>
        DocumentLinkStatusColor.For(status);
```

In `src/PinballWizard.Web/Components/Shared/DocumentList.razor`, replace lines 165-171:

```csharp
    private static Color LinkStatusColor(string? status) => status switch
    {
        "linked" or "manually_linked" => Color.Success,
        "failed" or "not_in_catalog" => Color.Error,
        "platform_generic" => Color.Warning,
        _ => Color.Default
    };
```

with:

```csharp
    private static Color LinkStatusColor(string? status) =>
        DocumentLinkStatusColor.For(status);
```

In `src/PinballWizard.Web/Components/Shared/MachineDetail.razor`, replace lines 347-354:

```csharp
    private static Color LinkStatusColor(string? status) => status switch
    {
        "Linked" or "linked"         => Color.Success,
        "PlatformGeneric" or "platform_generic" => Color.Warning,
        "Failed" or "failed"         => Color.Error,
        "NotInCatalog" or "not_in_catalog"   => Color.Error,
        _                            => Color.Default,
    };
```

with:

```csharp
    private static Color LinkStatusColor(string? status) =>
        DocumentLinkStatusColor.For(status);
```

In `src/PinballWizard.Web/Components/Shared/DocumentDetail.razor`, replace lines 242-248:

```csharp
    private static Color LinkStatusColor(string? status) => status switch
    {
        "linked" or "manually_linked" => Color.Success,
        "failed" or "not_in_catalog" => Color.Error,
        "platform_generic" => Color.Warning,
        _ => Color.Default
    };
```

with:

```csharp
    private static Color LinkStatusColor(string? status) =>
        DocumentLinkStatusColor.For(status);
```

- [ ] **Step 6: Build to verify all four sites compile**

Run: `dotnet build src/PinballWizard.Web`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Shared/DocumentLinkStatusColor.cs \
        tests/PinballWizard.Web.Tests/Components/Shared/DocumentLinkStatusColorTests.cs \
        src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor \
        src/PinballWizard.Web/Components/Shared/DocumentList.razor \
        src/PinballWizard.Web/Components/Shared/MachineDetail.razor \
        src/PinballWizard.Web/Components/Shared/DocumentDetail.razor
git commit -m "fix(ui) unify document link-status color into one closed-palette helper

NotInCatalog now red (was amber on triage); platform_generic now neutral
(was amber/blue). Replaces four divergent LinkStatusColor copies."
```

---

### Task 2: Recolor the three centralized status helpers

`JobStatusColor`, `CatalogHealthColors`, and `SourceStatusView` still emit amber (`Warning`) and blue (`Info`) as status colors.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Shared/JobStatusColor.cs:7-14`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/CatalogHealthColors.cs:14-21`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/SourceStatusView.cs:29-30`
- Create: `tests/PinballWizard.Web.Tests/Components/Shared/JobStatusColorTests.cs`
- Create: `tests/PinballWizard.Web.Tests/Components/Admin/CatalogHealthColorsTests.cs`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/SourceStatusViewTests.cs:33-37`

**Interfaces:**
- Consumes: nothing new.
- Produces: unchanged signatures (`JobStatusColor.For`, `CatalogHealthColors.ForFlag/ForFlags`, `SourceStatusView.Derive`); only color values change.

- [ ] **Step 1: Write the failing JobStatusColor test**

Create `tests/PinballWizard.Web.Tests/Components/Shared/JobStatusColorTests.cs`:

```csharp
using MudBlazor;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class JobStatusColorTests
{
    [Theory]
    [InlineData("Succeeded", "Success")]
    [InlineData("Running", "Success")]     // active → green (was Info/blue)
    [InlineData("Processing", "Success")]  // active → green (was Info/blue)
    [InlineData("Failed", "Error")]
    [InlineData("Degraded", "Error")]      // problem → red (was Warning/amber)
    [InlineData("Stopped", "Error")]       // per spec §4.2 default (reviewer may switch to Default)
    [InlineData("Queued", "Default")]
    public void For_MapsToClosedPalette(string status, string expectedColor)
    {
        Assert.Equal(expectedColor, JobStatusColor.For(status).ToString());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~JobStatusColorTests"`
Expected: FAIL — `Running` returns `Info`, `Degraded`/`Stopped` return `Warning`.

- [ ] **Step 3: Recolor `JobStatusColor.cs`**

Replace lines 7-14:

```csharp
    internal static Color For(string status) => status switch
    {
        "Succeeded" => Color.Success,
        "Running" or "Processing" => Color.Info,
        "Failed" => Color.Error,
        "Stopped" or "Degraded" => Color.Warning,
        _ => Color.Default,
    };
```

with:

```csharp
    internal static Color For(string status) => status switch
    {
        "Succeeded" or "Running" or "Processing" => Color.Success, // active/healthy
        "Failed" or "Degraded" or "Stopped" => Color.Error,        // problem/terminal
        _ => Color.Default,
    };
```

- [ ] **Step 4: Run to verify JobStatusColor passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~JobStatusColorTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing CatalogHealthColors test**

Create `tests/PinballWizard.Web.Tests/Components/Admin/CatalogHealthColorsTests.cs`:

```csharp
using MudBlazor;
using PinballWizard.Application.Catalog;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class CatalogHealthColorsTests
{
    [Theory]
    [InlineData(CatalogHealthFlag.Empty, "Error")]
    [InlineData(CatalogHealthFlag.NoManual, "Default")]   // informational → neutral (was amber)
    [InlineData(CatalogHealthFlag.EditionGap, "Default")] // informational → neutral (was amber)
    [InlineData(CatalogHealthFlag.Ok, "Success")]
    public void ForFlag_MapsToClosedPalette(CatalogHealthFlag flag, string expectedColor)
    {
        Assert.Equal(expectedColor, CatalogHealthColors.ForFlag(flag).ToString());
    }
}
```

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~CatalogHealthColorsTests"`
Expected: FAIL — `NoManual`/`EditionGap` return `Warning`.

- [ ] **Step 7: Recolor `CatalogHealthColors.cs`**

Replace lines 14-21:

```csharp
    public static Color ForFlag(CatalogHealthFlag flag) => flag switch
    {
        CatalogHealthFlag.Empty      => Color.Error,
        CatalogHealthFlag.NoManual   => Color.Warning,
        CatalogHealthFlag.EditionGap => Color.Warning,
        CatalogHealthFlag.Ok         => Color.Success,
        _                            => Color.Default,
    };
```

with:

```csharp
    public static Color ForFlag(CatalogHealthFlag flag) => flag switch
    {
        CatalogHealthFlag.Empty      => Color.Error,   // missing catalog → failure
        CatalogHealthFlag.NoManual   => Color.Default, // informational health flag → neutral
        CatalogHealthFlag.EditionGap => Color.Default, // informational health flag → neutral
        CatalogHealthFlag.Ok         => Color.Success,
        _                            => Color.Default,
    };
```

- [ ] **Step 8: Recolor `SourceStatusView.cs` (Deferred) + update its test**

In `src/PinballWizard.Web/Components/Pages/Admin/SourceStatusView.cs`, replace line 29-30:

```csharp
            "Deferred" => new SourceStatusView(
                SourceStatus.Deferred, "Deferred", Color.Warning, Icons.Material.Filled.PauseCircleOutline),
```

with:

```csharp
            "Deferred" => new SourceStatusView(
                SourceStatus.Deferred, "Deferred", Color.Default, Icons.Material.Filled.PauseCircleOutline),
```

In `tests/PinballWizard.Web.Tests/Components/Admin/SourceStatusViewTests.cs`, replace lines 33-37:

```csharp
    [Fact]
    public void Derive_Deferred_UsesWarningColour()
    {
        Assert.Equal(Color.Warning, SourceStatusView.Derive(false, "Deferred").Color);
    }
```

with:

```csharp
    [Fact]
    public void Derive_Deferred_UsesDefaultColour() // informational, not a failure (closed palette)
    {
        Assert.Equal(Color.Default, SourceStatusView.Derive(false, "Deferred").Color);
    }
```

- [ ] **Step 9: Run all three helpers' tests**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~JobStatusColorTests|FullyQualifiedName~CatalogHealthColorsTests|FullyQualifiedName~SourceStatusViewTests"`
Expected: PASS (all).

- [ ] **Step 10: Commit**

```bash
git add src/PinballWizard.Web/Components/Shared/JobStatusColor.cs \
        src/PinballWizard.Web/Components/Pages/Admin/CatalogHealthColors.cs \
        src/PinballWizard.Web/Components/Pages/Admin/SourceStatusView.cs \
        tests/PinballWizard.Web.Tests/Components/Shared/JobStatusColorTests.cs \
        tests/PinballWizard.Web.Tests/Components/Admin/CatalogHealthColorsTests.cs \
        tests/PinballWizard.Web.Tests/Components/Admin/SourceStatusViewTests.cs
git commit -m "fix(ui) recolor job/catalog-health/source status helpers to closed palette

Running/Processing → green; Degraded/Stopped → red; NoManual/EditionGap/Deferred
→ neutral. Removes amber (Warning) and blue (Info) from status badges."
```

---

### Task 3: Closed-palette guard test

A single anti-regression test asserting every centralized status helper resolves only to the three allowed status colors. This is the backstop that fails CI if anyone reintroduces amber/blue/teal as a status color.

**Files:**
- Create: `tests/PinballWizard.Web.Tests/Components/Shared/ClosedStatusPaletteTests.cs`

**Interfaces:**
- Consumes: `DocumentLinkStatusColor.For`, `JobStatusColor.For`, `CatalogHealthColors.ForFlag`, `SourceStatusView.Derive`.

- [ ] **Step 1: Write the guard test**

Create `tests/PinballWizard.Web.Tests/Components/Shared/ClosedStatusPaletteTests.cs`:

```csharp
using System;
using System.Linq;
using MudBlazor;
using PinballWizard.Application.Catalog;
using PinballWizard.Web.Components.Pages.Admin;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

// Guards the closed 5-role palette (design §4.1): status colors are only
// Success / Error / Default. Amber (Primary/Warning), blue (Info) and teal
// (Tertiary) are interactive-only or banned and must never be a status color.
public sealed class ClosedStatusPaletteTests
{
    private static readonly Color[] Allowed = { Color.Success, Color.Error, Color.Default };

    [Fact]
    public void DocumentLinkStatusColor_OnlyEmitsAllowedColors()
    {
        string?[] inputs =
        {
            "linked", "Linked", "manually_linked", "failed", "Failed",
            "not_in_catalog", "NotInCatalog", "platform_generic", "PlatformGeneric",
            "unknown", null,
        };
        Assert.All(inputs, s => Assert.Contains(DocumentLinkStatusColor.For(s), Allowed));
    }

    [Fact]
    public void JobStatusColor_OnlyEmitsAllowedColors()
    {
        string[] inputs = { "Succeeded", "Running", "Processing", "Failed", "Degraded", "Stopped", "Queued" };
        Assert.All(inputs, s => Assert.Contains(JobStatusColor.For(s), Allowed));
    }

    [Fact]
    public void CatalogHealthColors_OnlyEmitsAllowedColors()
    {
        Assert.All(
            Enum.GetValues<CatalogHealthFlag>(),
            f => Assert.Contains(CatalogHealthColors.ForFlag(f), Allowed));
    }

    [Fact]
    public void SourceStatusView_OnlyEmitsAllowedColors()
    {
        var views = new[]
        {
            SourceStatusView.Derive(true, null),
            SourceStatusView.Derive(false, "NoSource"),
            SourceStatusView.Derive(false, "Deferred"),
            SourceStatusView.Derive(false, null),
        };
        Assert.All(views, v => Assert.Contains(v.Color, Allowed));
    }
}
```

- [ ] **Step 2: Run to verify it passes (Tasks 1-2 already fixed the sources)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~ClosedStatusPaletteTests"`
Expected: PASS (4 facts).

- [ ] **Step 3: Commit**

```bash
git add tests/PinballWizard.Web.Tests/Components/Shared/ClosedStatusPaletteTests.cs
git commit -m "test(ui) guard: status helpers only emit closed-palette colors"
```

---

### Task 4: Remove blue informational alerts

Five `MudAlert Severity.Info` (blue) banners are purely informational ("only available against live Azure", "sign in as admin"). Blue is banned; these are not warnings/failures, so use the neutral `Severity.Normal` (not amber `Severity.Warning`).

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor:26`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobs.razor:51`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobDetail.razor:50`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor:66,85`

- [ ] **Step 1: Swap each `Severity.Info` to `Severity.Normal`**

In each file/line above, change `Severity="Severity.Info"` to `Severity="Severity.Normal"` on the `MudAlert`. (Text and all other attributes unchanged.)

- [ ] **Step 2: Verify no `Severity.Info` remains in admin pages**

Run: `grep -rn "Severity.Info" src/PinballWizard.Web/Components/Pages/Admin/`
Expected: no matches.

- [ ] **Step 3: Build**

Run: `dotnet build src/PinballWizard.Web`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminJobs.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminJobDetail.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor
git commit -m "fix(ui) neutral (not blue) informational admin alerts

Severity.Info → Severity.Normal on the five 'live-Azure-only' / 'sign in' banners
(no blue in the closed palette; informational, not a warning)."
```

---

### Task 5: Decouple `AppSummaryCard` CTA color from icon color

The dashboard CTA button inherits `IconColor`, so non-amber icons (Secondary/Tertiary/Warning/Info) produce gray/teal/amber/blue CTAs. Add a `ButtonColor` defaulting to `Color.Primary` (CTA always amber), fix the Tertiary caption text, and set dashboard icons to a palette-safe neutral.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Shared/AppSummaryCard.razor:12,18,24-33`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor:44-135`
- Create: `tests/PinballWizard.Web.Tests/Components/Shared/AppSummaryCardTests.cs`

**Interfaces:**
- Produces: `AppSummaryCard` gains `[Parameter] public Color ButtonColor { get; set; } = Color.Primary;`. The CTA `MudButton` uses `ButtonColor`; the `MudIcon` still uses `IconColor`.

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Web.Tests/Components/Shared/AppSummaryCardTests.cs`:

```csharp
using Bunit;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AppSummaryCardTests : AsyncBunitContext
{
    public AppSummaryCardTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Cta_IsAmberPrimary_EvenWhenIconColorIsNot()
    {
        var cut = Render<AppSummaryCard>(p => p
            .Add(x => x.Icon, Icons.Material.Filled.Storage)
            .Add(x => x.IconColor, Color.Info)          // deliberately non-amber icon
            .Add(x => x.Label, "Documents Indexed")
            .Add(x => x.ActionHref, "/admin/corpus")
            .Add(x => x.ActionLabel, "View corpus")
            .Add(x => x.Content, b => b.AddMarkupContent(0, "<span>42</span>")));

        // The CTA button carries the primary (amber) text-color class, not info/blue.
        var button = cut.Find("a.mud-button-root");
        var cls = button.GetAttribute("class") ?? "";
        Assert.Contains("mud-button-text-primary", cls);
        Assert.DoesNotContain("mud-button-text-info", cls);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppSummaryCardTests"`
Expected: FAIL — button renders `mud-button-text-info` (inherits `IconColor`).

- [ ] **Step 3: Add `ButtonColor` and fix the caption color**

In `src/PinballWizard.Web/Components/Shared/AppSummaryCard.razor`:

Line 12 — change the caption text color off teal:
```razor
                    <MudText Typo="Typo.caption" Color="Color.Tertiary">@Caption</MudText>
```
to:
```razor
                    <MudText Typo="Typo.caption" Color="Color.Secondary">@Caption</MudText>
```

Line 18 — the CTA button uses `ButtonColor`:
```razor
        <MudButton Href="@ActionHref" Variant="Variant.Text" Color="@IconColor" Size="Size.Small">
```
to:
```razor
        <MudButton Href="@ActionHref" Variant="Variant.Text" Color="@ButtonColor" Size="Size.Small">
```

In the `@code` block (after the `IconColor` parameter, line 26), add:
```csharp
    // CTA color is decoupled from the decorative icon color and defaults to the
    // amber interactive role. Icons may be neutral/expressive; the action is always amber.
    [Parameter] public Color ButtonColor { get; set; } = Color.Primary;
```

- [ ] **Step 4: Run to verify AppSummaryCard test passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppSummaryCardTests"`
Expected: PASS.

- [ ] **Step 5: Set dashboard card icons to neutral (remove teal/blue/amber icons)**

In `src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor`, change every `IconColor` on the six cards to `Color.Secondary` (decorative, neutral; CTA is amber via the new default). Specifically:
- line 46: `IconColor="Color.Primary"` → `IconColor="Color.Secondary"`
- line 61: `IconColor="Color.Secondary"` → (unchanged)
- line 76: `IconColor="Color.Tertiary"` → `IconColor="Color.Secondary"`
- line 92: `IconColor="Color.Warning"` → `IconColor="Color.Secondary"`
- line 107: `IconColor="Color.Info"` → `IconColor="Color.Secondary"`
- line 123: `IconColor="Color.Primary"` → `IconColor="Color.Secondary"`

(No `ButtonColor` is passed, so all six CTAs default to amber `Color.Primary`.)

- [ ] **Step 6: Build**

Run: `dotnet build src/PinballWizard.Web`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Shared/AppSummaryCard.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor \
        tests/PinballWizard.Web.Tests/Components/Shared/AppSummaryCardTests.cs
git commit -m "fix(ui) dashboard CTAs always amber; neutral icons; no teal/blue

AppSummaryCard gains ButtonColor (default Primary) decoupled from IconColor;
caption off Color.Tertiary; all six card icons → Secondary."
```

---

### Task 6: Trim zero-value columns (#9)

Drop the Documents "Format" column (every row is `html`) and fold Triage's "Document ID" into the Link Text cell as a caption line.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Shared/DocumentList.razor:67`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor:57,60`
- Create: `tests/PinballWizard.Web.Tests/Components/Shared/DocumentListColumnsTests.cs`

**Interfaces:**
- Consumes: `PinballWizard.Application.Documents.DocumentListItem` (public record — see its ctor in `src/PinballWizard.Application/Documents/DocumentListItem.cs`), `IRawDocumentRepository` (mocked).

- [ ] **Step 1: Write the failing test (DocumentList has no "Format" column)**

Create `tests/PinballWizard.Web.Tests/Components/Shared/DocumentListColumnsTests.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using Bunit;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class DocumentListColumnsTests : AsyncBunitContext
{
    public DocumentListColumnsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        // DocumentList streams its rows in OnInitializedAsync via
        // Repo.StreamDocumentsAsync(game, manufacturer, type, includeAdminFields, ct).
        var repo = Substitute.For<IRawDocumentRepository>();
        repo.StreamDocumentsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Empty());
        Services.AddSingleton(repo);
        Services.AddSingleton(NullLogger<DocumentList>.Instance);
    }

    // The grid renders its column headers regardless of row count, so an empty
    // stream is enough to assert the header set.
    private static async IAsyncEnumerable<DocumentListItem> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Fact]
    public void Grid_HasNoFormatColumn()
    {
        var cut = Render<DocumentList>(p => p.Add(x => x.IsAdmin, true));
        cut.WaitForAssertion(() => Assert.DoesNotContain(">Format<", cut.Markup));
    }
}
```

> **Executor note:** MudDataGrid renders a column title as text inside the header cell; if the `>Format<` matcher is brittle against the actual header DOM, assert instead that no header cell (`.mud-table-cell` in the `<thead>`) has `TextContent == "Format"`. `System.Threading.Tasks` is pulled in transitively via the async iterator; add the `using` if the compiler asks.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~DocumentListColumnsTests"`
Expected: FAIL — markup still contains the "Format" header.

- [ ] **Step 3: Delete the Format column**

In `src/PinballWizard.Web/Components/Shared/DocumentList.razor`, delete line 67:
```razor
            <PropertyColumn Property="x => x.FileFormat" Title="Format" />
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~DocumentListColumnsTests"`
Expected: PASS.

- [ ] **Step 5: Fold Triage "Document ID" into the Link Text cell**

In `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor`, delete the standalone Document ID column (line 57):
```razor
            <PropertyColumn Property="x => x.DocumentId" Title="Document ID" />
```

Replace the Link Text `PropertyColumn` (line 60):
```razor
            <PropertyColumn Property="x => x.LinkText" Title="Link Text" />
```
with a `TemplateColumn` that shows the link text with the id as a caption line:
```razor
            <TemplateColumn Title="Link Text">
                <CellTemplate>
                    <MudText Typo="Typo.body2">@context.Item.LinkText</MudText>
                    <MudText Typo="Typo.caption" Color="Color.Secondary"
                             data-testid="triage-doc-id">@context.Item.DocumentId</MudText>
                </CellTemplate>
            </TemplateColumn>
```

- [ ] **Step 6: Build**

Run: `dotnet build src/PinballWizard.Web`
Expected: Build succeeded (the triage grid drops from 8 to 7 columns).

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Shared/DocumentList.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor \
        tests/PinballWizard.Web.Tests/Components/Shared/DocumentListColumnsTests.cs
git commit -m "fix(ui) drop zero-value columns

Documents 'Format' column removed (always html); Triage 'Document ID' folded
into the Link Text cell as a caption (8→7 columns)."
```

---

### Task 7: Comma-format counts (#10)

`AdminCountValue` renders `@Count` raw; several list columns render raw integers. Apply `N0`.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Shared/AdminCountValue.razor:29`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor:89`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor:113`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor:91,92`
- Modify: `src/PinballWizard.Web/Components/Shared/DocumentList.razor` (Pages column, was line 68)
- Create: `tests/PinballWizard.Web.Tests/Components/Shared/AdminCountValueTests.cs`

**Interfaces:**
- Consumes: `AdminCountValue` public parameters (`Count:int?`, `Loading:bool`, `Failed:bool`, `TestId:string`).

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Web.Tests/Components/Shared/AdminCountValueTests.cs`:

```csharp
using Bunit;
using MudBlazor.Services;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class AdminCountValueTests : AsyncBunitContext
{
    public AdminCountValueTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void CommaFormatsLargeCount()
    {
        var cut = Render<AdminCountValue>(p => p
            .Add(x => x.TestId, "c")
            .Add(x => x.Loading, false)
            .Add(x => x.Failed, false)
            .Add(x => x.Count, 30875));
        Assert.Contains("30,875", cut.Find("[data-testid='c']").TextContent);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminCountValueTests"`
Expected: FAIL — renders `30875`.

- [ ] **Step 3: Format the count**

In `src/PinballWizard.Web/Components/Shared/AdminCountValue.razor`, line 29:
```razor
        <MudText Typo="Typo.h5" data-testid="@TestId">@Count</MudText>
```
to:
```razor
        <MudText Typo="Typo.h5" data-testid="@TestId">@Count?.ToString("N0")</MudText>
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminCountValueTests"`
Expected: PASS.

- [ ] **Step 5: Format the raw list columns**

Apply `N0` to these numeric columns (each is currently a raw `PropertyColumn` or `@context` int). Convert to a formatted `TemplateColumn` (or add a `PropertyColumn` `Format`/formatted cell) as fits the surrounding code:
- `AdminManufacturers.razor:89` — Machines count `@context.Item.Machines` → `@context.Item.Machines.ToString("N0")`
- `AdminMachines.razor:113` — DocCount → `.ToString("N0")`
- `AdminSources.razor:91` — DocsDiscovered (long) → `.ToString("N0")`
- `AdminSources.razor:92` — RunFailures (long) → `.ToString("N0")`
- `DocumentList.razor` Pages column (the `PageCount` `PropertyColumn`, previously line 68) → render `@context.Item.PageCount?.ToString("N0")` in a `TemplateColumn` keeping `HeaderClass/CellClass="text-right"`.

> **Executor note:** for `PropertyColumn`s, prefer MudBlazor's `Format="N0"` attribute where the column stays a `PropertyColumn`; switch to a `TemplateColumn` only where the value is nullable and needs `?.ToString("N0")`. Match the existing column kind per file.

- [ ] **Step 6: Build + verify counts render formatted**

Run: `dotnet build src/PinballWizard.Web`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Shared/AdminCountValue.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor \
        src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor \
        src/PinballWizard.Web/Components/Shared/DocumentList.razor \
        tests/PinballWizard.Web.Tests/Components/Shared/AdminCountValueTests.cs
git commit -m "fix(ui) comma-format admin counts (N0)

AdminCountValue + Manufacturers/Machines/Sources/DocumentList numeric columns."
```

---

### Task 8: Full-suite verification + PR

- [ ] **Step 1: Run the CI-equivalent suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: all pass. (Circuit/A11y/Snapshot categories run in CI ui-tests, not locally — `reference_circuit_tests_ci_only`.)

- [ ] **Step 2: Pre-push self-audit**

Run `/local-review` (qualitative) and `/standards-audit` (mechanical). Treat 🔴 as blocking; fix before push.

- [ ] **Step 3: Capture before/after screenshots** of `/admin` (dashboard CTAs), `/admin/document-triage` (columns + NotInCatalog now red), `/admin/jobs` (status colors) for the PR description.

- [ ] **Step 4: Push + open PR** via `gh pr create`; add + verify the `claude-code` label; put the full PR URL in the reply; record `/local-review` + `/standards-audit` outcomes in the description; then triage post-push code-scanning findings (PR-AUDIT Step 2) and watch the post-merge `Deploy` to green (Step 3).

---

## Self-Review

**Spec coverage (§4):** §4.1 palette → Tasks 1-3 + guard; §4.2 status recolors → Tasks 1-2; `AppSummaryCard` CTA decouple → Task 5; `Severity.Info` swap → Task 4; §4.3 column trims → Task 6; §4.4 number formatting → Task 7; §4.5 tests → helper tests + guard + component tests across Tasks 1-7. All covered.

**Placeholder scan:** No TBD/TODO. Two "Executor note" callouts (NSubstitute-vs-Moq confirmation; PropertyColumn `Format` vs TemplateColumn) name a concrete default action + exactly what to verify — not deferred work.

**Type consistency:** `DocumentLinkStatusColor.For(string?)` defined Task 1, consumed Tasks 1/3. `JobStatusColor.For`, `CatalogHealthColors.ForFlag`, `SourceStatusView.Derive` signatures unchanged (Task 2), reused Task 3. `AppSummaryCard.ButtonColor` defined Task 5. `DocumentListItem` ctor matches `src/PinballWizard.Application/Documents/DocumentListItem.cs`.
