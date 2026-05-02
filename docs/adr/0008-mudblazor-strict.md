# 0008 — MudBlazor strict — single UI component library

**Status:** Accepted
**Date:** 2026-05-02

## Context

Phase 5 adds a Blazor Web App as the public face of pinwiz.ai. Blazor
has a healthy ecosystem of UI component libraries — MudBlazor, Radzen,
Syncfusion, Telerik, Microsoft Fluent UI Blazor, and many smaller
specialty libraries.

It's tempting to mix and match: "use MudBlazor for the data grid but
Radzen's chart because it has better tooltips," or "drop in a
hand-rolled `<MapBox>` component for the location map because no
Blazor library renders Mapbox well."

That path leads to:

- Conflicting CSS resets that fight each other across components
- Multiple theming systems (MudBlazor uses its own theme provider;
  Radzen has another; Fluent UI has yet another) — keeping them in
  visual sync is fragile work
- Inflated bundle size — every additional component library pulls in
  its own JS / CSS / icon set
- Maintenance debt — keeping multiple libraries' dependencies updated
  across breaking changes
- Inconsistent UX — keyboard navigation, focus management, and
  accessibility patterns differ across libraries

## Decision

**MudBlazor is the single UI component library**. Strict.

All Blazor UI uses MudBlazor components: `MudDataGrid`, `MudPaper`,
`MudChart`, `MudStepper`, `MudTabs`, `MudDialog`, `MudSnackbar`, etc.
No mixing in MUI / Radzen / Syncfusion / hand-rolled component sets.

If MudBlazor doesn't have a component we need:

1. **First**: check MudBlazor.Extensions or active community
   extensions that follow MudBlazor's theming and accessibility
   patterns.
2. **Second**: build the missing component as a thin wrapper around a
   primitive HTML element + MudBlazor's theme tokens, in our own
   `PinballWizard.Web/Components/` namespace. Style it using
   MudBlazor's theme variables so it looks native.
3. **Last resort**: vendor in a plain JS/CSS solution (e.g., Mapbox
   GL JS for the location map). Vendor with tight surface — wrap it
   in a Blazor component that exposes a small, idiomatic API and
   hides the JS interop.

Adding a second Blazor component library would require its own ADR
that supersedes or amends this one.

## Consequences

**Positive:**
- **One theming system.** Light / dark mode, palette changes,
  typography decisions apply consistently across the app via
  `MudThemeProvider`.
- **One accessibility baseline.** MudBlazor's components have
  consistent ARIA attributes, keyboard handling, and focus
  management. We don't have to audit a second library.
- **One mental model for developers.** Component naming, parameter
  conventions, and event handlers are uniform. New code is easier to
  write and review.
- **Smaller bundle.** Single CSS / JS surface; no duplicate icon sets.
- **Predictable upgrade path.** One library to keep current.

**Negative:**
- **Library-specific lock-in.** A future migration off MudBlazor
  would touch every page. We accept this — MudBlazor is well-maintained
  and broadly adopted; the lock-in cost is not paid until we hit a
  library-level dealbreaker, which would warrant the migration anyway.
- **Some specialty needs require fallback.** Maps and rich charts
  beyond MudBlazor's chart capabilities will require third-party JS
  libraries. The "wrap in a Blazor component" rule keeps that
  manageable.
- **Slower initial development on components MudBlazor doesn't
  cover.** A few hours wrapping a JS library beats a few weeks of
  cross-library theming debt.

## Alternatives considered

- **Pick the best library per use case.** Rejected for the conflict /
  bundle / accessibility / maintenance reasons above. The "just one
  library" decision is a load-bearing simplification, not a
  preference.
- **Fluent UI Blazor instead of MudBlazor.** Considered. Fluent is
  excellent and Microsoft-backed, but MudBlazor's data grid and
  charting components are more mature and the project's design
  language fits Material more naturally than Fluent.
- **Roll our own component library.** Rejected — vast scope, no
  payoff for a hobby project, and reinventing accessibility primitives
  is a career's worth of work done badly.

## References

- [`docs/infra_analysis.md`](../infra_analysis.md) §1 — MudBlazor
  noted as the strict UI library.
- [`project_phase2_architecture_decisions.md`](../../../../Users/JimKeeley/.claude/projects/c--projects-PinballWizard/memory/project_phase2_architecture_decisions.md)
  — MudBlazor strict locked decision.
