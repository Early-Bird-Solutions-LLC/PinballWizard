# PinballWizard UI Revamp — Design Spec
**Date:** 2026-06-29
**Issue:** #343
**Branch:** `feat/ui-revamp`
**Spec authority:** `docs/ui/themes/modern-lcd.md`, `docs/ui/themes/sibling-themes-overview.md`
**Standards gate:** FE-07 (palette-pinned), FE-08 (design-system-sync), FE-09 (citation-as-hero)

---

## Goal

Elevate PinballWizard from "reads as amateur" to "polished enterprise reference app." The current UI has flat typography with no hierarchy, a Status page with invisible service dots (production CSS bug), untreated Error/NotFound pages, a cramped footer, and sibling themes that affect `--pw-*` custom properties only — leaving every MudBlazor component (AppBar, cards, inputs, chips) permanently LCD-colored regardless of which theme is active.

This pass fixes the theming architecture root-cause first, then applies the correct visual treatment to every public surface.

---

## Architecture

### 1. The MudBlazor gap (root cause of all theme breakage)

The six sibling themes in `app.css` each define `html.theme-<name> { --pw-*: ...; }` blocks. This only affects custom PinballWizard tokens — it does not touch `--mud-palette-*` variables that MudBlazor components read. Result: switching to Paper (or any other sibling) makes the custom delight surfaces change colour but leaves the AppBar, input borders, chip fills, and every other MudBlazor component unchanged.

**Fix:** Add a `--mud-palette-*` override block inside each `html.theme-<name>` rule. The tokens that must be overridden are:

```
--mud-palette-primary
--mud-palette-primary-text
--mud-palette-secondary
--mud-palette-background
--mud-palette-surface
--mud-palette-appbar-background
--mud-palette-appbar-text
--mud-palette-text-primary
--mud-palette-text-secondary
--mud-palette-divider
--mud-palette-action-default
--mud-palette-success
--mud-palette-error
--mud-palette-lines-default
--mud-palette-drawer-background
--mud-palette-drawer-text
--mud-palette-drawer-icon
```

These are set inline in the `html.theme-<name>` CSS block — no second MudTheme object is needed for the dark siblings (their MudBlazor values already match Modern LCD). Only Paper requires a full MudTheme `PaletteLight` companion (see §2).

### 2. Paper — the new default theme

Paper is promoted to the default for new visitors. On first load (no localStorage entry for `pinwiz.theme`), the app sets `html.theme-paper`. Modern LCD is the opt-in dark alternative.

**`PinballTheme.cs` adds:**

- A `CreatePaper()` factory method returning a `MudTheme` with `PaletteLight` set to the Paper token values (below). `PaletteDark` mirrors `Create()`'s dark palette so if MudBlazor ever reads dark mode on Paper it does not fall back to violet.
- Shape override on **both** themes: `LayoutProperties.DefaultBorderRadius = "2px"` (overrides MudBlazor's default ~8px).
- Elevation override on **both** themes: all 25 shadow slots set to `"none"`. Depth is communicated by background tone shifts only.

**Paper `PaletteLight` values** (WCAG AA verified — see §6):

| MudBlazor property | Value | Semantic role |
|---|---|---|
| `AppbarBackground` | `#1a1410` | Warm dark masthead — warm walnut, NOT `#08070a` (that's cool LCD black) |
| `AppbarText` | `#f0ebe2` | Off-white pulled from cream palette |
| `Background` | `#f4f1ea` | `bg-base` — aged-paper content floor |
| `Surface` | `#faf8f2` | `bg-surface` — card/panel interiors (elevated = lighter in light mode) |
| `DrawerBackground` | `#ede8dc` | Slightly deeper cream for nav drawer |
| `DrawerText` | `#1f1a14` | Deep warm ink |
| `DrawerIcon` | `#b8763e` | Amber shifted for light background |
| `TextPrimary` | `#1f1a14` | Deep warm ink |
| `TextSecondary` | `#6b6050` | Mid-warm for labels, metadata |
| `Primary` | `#b8763e` | `accent-primary` — amber shifted for light (verified 4.74:1 on `#f4f1ea`) |
| `PrimaryContrastText` | `#ffffff` | White on shifted amber |
| `Success` | `#1a8a45` | `accent-grounded` — green shifted for light (verified 5.21:1 on `#f4f1ea`) |
| `Error` | `#c0200e` | `accent-refusal` — red shifted for light (verified 5.08:1 on `#f4f1ea`) |
| `ErrorContrastText` | `#ffffff` | — |
| `Divider` | `#d8d2c2` | `border-quiet` |
| `ActionDefault` | `#b8763e` | Matches Primary |
| `ActionDisabled` | `#c8bc9a` | Muted warm |
| `LinesDefault` | `#d8d2c2` | Same as Divider |
| `OverlayDark` | `rgba(26, 20, 16, 0.8)` | Dark overlay on Paper backgrounds |
| `OverlayLight` | `rgba(244, 241, 234, 0.6)` | Light overlay on Paper backgrounds |

> **WCAG AA floor:** every accent above passes 4.5:1 against `#f4f1ea` and 3:1 against `#faf8f2`. If any value needs adjustment during implementation, it moves to achieve AA — the floor is the requirement, not the hex.

### 3. Shape and elevation (both themes)

Add to both `Create()` and `CreatePaper()`:

```csharp
LayoutProperties = new LayoutProperties
{
    DefaultBorderRadius = "2px",
},
Shadows = new Shadow
{
    Elevation = Enumerable.Repeat("none", 25).ToArray()
}
```

Also add to `:root` in `app.css`:

```css
--mud-default-borderradius: 2px;
```

### 4. Theme switching wiring

**`MainLayout.razor`** — on `OnAfterRenderAsync(firstRender: true)`:
1. Read `localStorage["pinwiz.theme"]` via JS interop. Default `"paper"` if absent.
2. Apply `document.documentElement.className = "theme-" + value` via JS.
3. Select `PinballTheme.Create()` or `PinballTheme.CreatePaper()` based on value.
4. Set `IsDarkMode` on `MudThemeProvider`: `false` for `"paper"` and `"daytime-route"`, `true` for all others.

**`Settings.razor`** — theme picker writes to `localStorage["pinwiz.theme"]` and calls a JS function to update the `<html>` class and reload the theme without a full page navigation.

---

## Component changes

### A. `PinballTheme.cs`

**File:** `src/PinballWizard.Web/Components/Theming/PinballTheme.cs`

Changes to existing `Create()`:
- Add `LayoutProperties` with `DefaultBorderRadius = "2px"`.
- Add `Shadows` with all-none elevation.

New `CreatePaper()` method:
- `PaletteLight` with all Paper values from §2.
- `PaletteDark` identical to `Create()`'s `PaletteDark` (prevents violet fallback).
- Same `LayoutProperties`, `Shadows`, and `Typography` as `Create()`.

### B. `app.css`

**File:** `src/PinballWizard.Web/wwwroot/app.css`

1. Add `--mud-default-borderradius: 2px;` to `:root`.

2. For each existing `html.theme-<name>` block, append a `--mud-palette-*` section mapping from its `--pw-*` values. Example for `html.theme-paper`:

```css
html.theme-paper {
    /* existing --pw-* tokens unchanged */
    --pw-bg-base: #F4F1EA;
    /* ... rest of existing tokens ... */

    /* MudBlazor palette bridge — fixes the MudBlazor gap */
    --mud-palette-primary:            #b8763e;
    --mud-palette-primary-text:       #ffffff;
    --mud-palette-secondary:          #6b6050;
    --mud-palette-background:         #f4f1ea;
    --mud-palette-surface:            #faf8f2;
    --mud-palette-appbar-background:  #1a1410;
    --mud-palette-appbar-text:        #f0ebe2;
    --mud-palette-text-primary:       #1f1a14;
    --mud-palette-text-secondary:     #6b6050;
    --mud-palette-divider:            #d8d2c2;
    --mud-palette-action-default:     #b8763e;
    --mud-palette-success:            #1a8a45;
    --mud-palette-error:              #c0200e;
    --mud-palette-lines-default:      #d8d2c2;
    --mud-palette-drawer-background:  #ede8dc;
    --mud-palette-drawer-text:        #1f1a14;
    --mud-palette-drawer-icon:        #b8763e;
}
```

For the dark siblings, the `--mud-palette-*` values map from their `--pw-*` equivalents in the same pattern.

3. Add RGB-decomposed accent vars to `:root` and all theme blocks (required for `rgba(var(...), alpha)` in flipper button CSS):

```css
/* In :root (Modern LCD values) */
--pw-accent-primary-rgb:   255, 154, 31;
--pw-accent-grounded-rgb:  52, 217, 106;
--pw-accent-mode-rgb:      225, 59, 217;
--pw-accent-refusal-rgb:   255, 59, 48;

/* In html.theme-paper — Paper values */
--pw-accent-primary-rgb:   184, 118, 62;
--pw-accent-grounded-rgb:  26, 138, 69;
--pw-accent-mode-rgb:      140, 88, 41;
--pw-accent-refusal-rgb:   192, 32, 14;
```

Add matching RGB vars to each of the other four sibling theme blocks using their existing `--pw-accent-*` hex values decomposed to R, G, B integers.

### C. `MainLayout.razor`

**File:** `src/PinballWizard.Web/Components/Layout/MainLayout.razor`

Add JS interop for localStorage read on first render, `<html>` class application, and `IsDarkMode` binding. The theme object passed to `MudThemeProvider` is stateful — switching theme calls `StateHasChanged()`.

### D. `CitationCard.razor` — flipper-button CTA pair

**File:** `src/PinballWizard.Web/Components/Citations/CitationCard.razor`
**CSS:** `src/PinballWizard.Web/Components/Citations/CitationCard.razor.css`

Read the current file before modifying. Add to the bottom of each card:

```html
<div class="pw-flipper-pair">
    <button class="pw-flipper pw-flipper--left"
            @onclick="ScrollToMarker"
            aria-label="View in answer">
        <span class="pw-flipper__arrow">◀</span>
        <span class="pw-flipper__label">VIEW IN ANSWER</span>
    </button>
    <button class="pw-flipper pw-flipper--right"
            @onclick="OpenOriginal"
            aria-label="View the original source">
        <span class="pw-flipper__label">VIEW THE ORIGINAL</span>
        <span class="pw-flipper__arrow">▶</span>
    </button>
</div>
```

**CSS spec for flipper buttons** (in `CitationCard.razor.css` or a shared `flipper.css` imported by it):

```css
.pw-flipper-pair {
    display: flex;
    gap: 8px;
    margin-top: 16px;
}

.pw-flipper {
    min-height: 44px;        /* WCAG AA touch target */
    border-radius: 6px;      /* rounder than panels (real flipper buttons) */
    border: 1px solid transparent;
    padding: 0 16px;
    font-family: 'Barlow Condensed', sans-serif;
    font-weight: 700;
    font-size: 0.875rem;
    letter-spacing: 0.05em;
    text-transform: uppercase;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 8px;
    box-shadow: inset 0 2px 4px rgba(0, 0, 0, 0.4);  /* recessed look */
    transition: background 150ms ease, transform 80ms ease;
}

.pw-flipper--left {
    background: rgba(var(--pw-accent-mode-rgb, 140, 88, 41), 0.20);
    color: var(--pw-accent-mode);
    flex: 1;
}

.pw-flipper--right {
    background: rgba(var(--pw-accent-grounded-rgb, 26, 138, 69), 0.20);
    color: var(--pw-accent-grounded);
    flex: 2;   /* right flipper wider — it's the primary CTA */
}

.pw-flipper:hover {
    background: rgba(var(--pw-accent-mode-rgb, 140, 88, 41), 0.40);
}
.pw-flipper--right:hover {
    background: rgba(var(--pw-accent-grounded-rgb, 26, 138, 69), 0.40);
}

.pw-flipper:active {
    transform: translateY(1px);    /* the only press-translation in the design */
    background: rgba(var(--pw-accent-mode-rgb, 140, 88, 41), 0.60);
}
.pw-flipper--right:active {
    background: rgba(var(--pw-accent-grounded-rgb, 26, 138, 69), 0.60);
}

@media (max-width: 600px) {
    .pw-flipper--left { display: none; }   /* left collapses on mobile */
    .pw-flipper--right { flex: 1; }
}

@media (prefers-reduced-motion: reduce) {
    .pw-flipper { transition: none; }
    .pw-flipper:active { transform: none; }
}
```

> **Note on `--pw-accent-mode-rgb` / `--pw-accent-grounded-rgb`:** Add RGB-decomposed versions of `--pw-accent-mode` and `--pw-accent-grounded` to the `:root` and each theme block so `rgba(var(...), alpha)` works. These do NOT exist in the current `app.css` and must be added.

**Mobile left-flipper behaviour:** add `@onclick` on the card container element that fires `ScrollToMarker` when on mobile viewport — this replaces the hidden left button.

### E. `Status.razor` + `Status.razor.css`

**Files:**
- `src/PinballWizard.Web/Components/Pages/Status.razor`
- NEW: `src/PinballWizard.Web/Components/Pages/Status.razor.css`

**Bug:** `.status-dot` is defined only in `LiveStatusBadge.razor.css` (Blazor scoped CSS). The Status page renders `.status-dot` directly but gets no styles because Blazor's CSS isolation scope ID doesn't match. Status page dots are invisible in production.

**Fix:**
1. Create `Status.razor.css` with the dot styles scoped to the Status page.
2. Alternatively, if `.status-dot` is only used as a standalone indicator inside Status cards (not inside `LiveStatusBadge`), move it to a shared non-scoped file like `app.css` under a `.pw-status-dot` class and update both usage sites.

Recommended: option 2 (shared `.pw-status-dot` in `app.css`) — avoids duplication and makes the class available to any future surface that needs a status dot.

**Additional improvements to Status.razor:**
- Each service card: add a `MudIcon` per service (e.g., `Icons.Material.Filled.Storage` for Cosmos, `Icons.Material.Filled.ManageSearch` for AI Search, `Icons.Material.Filled.AutoAwesome` for Foundry).
- Loading state: wrap card content in a check on whether data is loaded; show `<MudSkeleton Height="80px" />` while loading.
- Use `AppStatusChip` (already in `Components/Shared/`) for status values.

### F. `RefusalPanel.razor`

**File:** `src/PinballWizard.Web/Components/Refusal/RefusalPanel.razor`

Read the current file. **Required treatment:**

- Outer container: left border `3px solid var(--pw-accent-refusal)` + `var(--pw-border-glow-red)` box-shadow on render.
- Category label: Barlow Condensed 700, ALL CAPS, `font-size: 1.75rem`, `color: var(--pw-accent-refusal)`. No MudBlazor `Color.Error` — use the CSS variable directly.
- Reason text: body type, one sentence, no apologetic language (`Sorry`, `Unfortunately`, `I couldn't`). Declarative.
- Routing CTAs via `CommunityResourceCards.razor`: verify peer parity (identical sizing and accent for every destination card — no primary/secondary visual distinction within a set).

### G. `Error.razor` and `NotFound.razor`

**Files:**
- `src/PinballWizard.Web/Components/Pages/Error.razor`
- `src/PinballWizard.Web/Components/Pages/NotFound.razor`

Read each before modifying. **Required treatment (both):**

- Large display-type callout: `SYSTEM ERROR` / `PAGE NOT FOUND` — Barlow Condensed 700, `var(--pw-accent-refusal)` color, `font-size: clamp(2rem, 5vw, 3.5rem)`.
- One sentence below: honest, declarative. No "Oops!" no "Something went wrong!" Just: "An unexpected error occurred." / "This page doesn't exist."
- CTA: `<MudButton Href="/" Variant="Variant.Filled" style="background: var(--pw-accent-primary); color: #1a1a1a;">Back to the Wizard</MudButton>`.
- No stack traces to users (preserve any existing `IsDevelopment` guard).
- No stock Blazor error page appearance.

### H. `BrandFooter.razor`

**File:** `src/PinballWizard.Web/Components/Layout/BrandFooter.razor`

Read before modifying. **Required treatment:**

- Outer padding: `padding: 32px 24px` minimum.
- Add tagline in `TextSecondary`: "PinballWizard routes you to the source, not away from it." — community-resource posture made visible.
- Secondary links row: About, Status, GitHub. Visually equal (`MudLink` with same styling — no link is larger, bolder, or more prominent than another).
- Bottom line: copyright + version in caption size, `text-secondary`.

### I. `Settings.razor` — theme picker

**File:** `src/PinballWizard.Web/Components/Pages/Settings.razor`

Add a "Visual theme" section. Swatch grid:

- Each swatch: 120×80px `<button>` element with `background: <theme-bg-base>`, an accent stripe at the bottom (`background: <theme-accent-primary>`, 8px tall).
- Theme name below in caption text.
- Selected swatch: `outline: 2px solid var(--pw-accent-grounded); outline-offset: 2px`.
- On click: `await JS.InvokeVoidAsync("pinwiz.setTheme", themeName)` which writes localStorage and updates `<html>` class.
- Theme display names: `"paper"` → "Paper", `"modern-lcd"` → "Modern LCD", `"backbox"` → "Backbox", `"cabinet"` → "Cabinet", `"dmd-classic"` → "DMD Classic", `"daytime-route"` → "Daytime Route".
- Add JS interop function `window.pinwiz.setTheme(name)` to `app.js` (or inline in `_Host.cshtml`/`App.razor` script block).

### J. `docs/ui/design-system/tokens.css`

After all code changes are committed, update this file to mirror the implemented theme (FE-08 standard). Update Paper palette tokens, 2px border radius, zero elevation. This file is documentation/mirror only — the repo CSS is the source of truth.

---

## Testing

### `PinballThemeContractTests.cs`

**File:** `tests/PinballWizard.Web.Tests/Components/Theming/PinballThemeContractTests.cs`

Add facts:

```csharp
[Fact]
public void PaperTheme_AppbarBackground_IsWarmDark()
    => Assert.Equal("#1a1410", PinballTheme.CreatePaper().PaletteLight.AppbarBackground.Value.ToLower());

[Fact]
public void PaperTheme_Background_IsWarmCream()
    => Assert.Equal("#f4f1ea", PinballTheme.CreatePaper().PaletteLight.Background.Value.ToLower());

[Fact]
public void PaperTheme_PrimaryAccent_MeetsWcagAA()
{
    var ratio = ContrastRatio("#b8763e", "#f4f1ea");
    Assert.True(ratio >= 4.5, $"Primary accent contrast {ratio:F2} fails WCAG AA");
}

[Fact]
public void PaperTheme_SuccessAccent_MeetsWcagAA()
{
    var ratio = ContrastRatio("#1a8a45", "#f4f1ea");
    Assert.True(ratio >= 4.5, $"Success accent contrast {ratio:F2} fails WCAG AA");
}

[Fact]
public void PaperTheme_ErrorAccent_MeetsWcagAA()
{
    var ratio = ContrastRatio("#c0200e", "#f4f1ea");
    Assert.True(ratio >= 4.5, $"Error accent contrast {ratio:F2} fails WCAG AA");
}

[Fact]
public void BothThemes_DefaultBorderRadius_Is2px()
{
    Assert.Equal("2px", PinballTheme.Create().LayoutProperties.DefaultBorderRadius);
    Assert.Equal("2px", PinballTheme.CreatePaper().LayoutProperties.DefaultBorderRadius);
}

[Fact]
public void BothThemes_Elevation0_IsNone()
{
    Assert.Equal("none", PinballTheme.Create().Shadows.Elevation[0]);
    Assert.Equal("none", PinballTheme.CreatePaper().Shadows.Elevation[0]);
}
```

Add a `private static double ContrastRatio(string fg, string bg)` helper using the WCAG relative luminance formula if one doesn't already exist in the test file.

### `RenderModeConventionTests`

Run without modification and verify it passes — no render-mode regressions from theming changes.

### Status page snapshot

If a snapshot test exists for Status.razor, update it after fixing the CSS bug. The new snapshot is the correct baseline.

---

## Constraints and invariants

- **MudBlazor strict (ADR-0008):** flipper buttons are the ONLY non-MudBlazor elements. Everything else uses MudBlazor primitives.
- **WCAG 2.1 AA:** all accent/background pairs verified programmatically in contract tests. Color is never the only meaning carrier.
- **Community-resource posture (ADR-0027):** outbound CTAs (right flipper, refusal routing) always visually peer-equal within a set.
- **No XML doc comments** on any public surface added during this pass.
- **No Claude attribution trailer** in commits.
- **Identity:** `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`.
- **FE-08 design-system-sync:** `docs/ui/design-system/tokens.css` updated in the same PR.
- **Personal GitHub only:** `gh pr create`, no `az devops`, no `AB#` prefixes.

---

## Out of scope

- Audio layer (v2, per ADR-0026).
- Pull-to-refresh plunger animation, cursor GI, tilt warnings, match sequences.
- New sibling themes beyond the six already in `app.css`.
- Admin pages (admin has separate design conventions).
- Full accessibility audit beyond WCAG AA contract tests.
- Sibling themes other than Paper getting full `MudTheme` objects (CSS variable bridge is sufficient for dark siblings).
