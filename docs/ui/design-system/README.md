# PinballWizard Design System — "Modern LCD"

The visual language of [PinballWizard](https://pinwiz.ai), Earlybird Solutions' customer-facing
showcase app. Modern LCD reads as *contemporary* pinball — "this is what 2026 pinball looks like" —
not retro nostalgia. The chrome looks like it came from the same workshop that built every machine
on every route: the universal grammar of cabinets, DMDs, and flipper buttons, never any one brand.

## Source of truth

This project is a **mirror** of the implemented theme. The authoritative definitions live in the repo:

- **Palette + typography** — `src/PinballWizard.Web/Components/Theming/PinballTheme.cs` (MudBlazor `MudTheme`)
- **Tokens MudPalette can't model** (surface-hi, accent-mode, border-glow tints, fonts, motion) —
  `src/PinballWizard.Web/wwwroot/app.css` `:root`
- **Design authority** (rationale, citation-as-hero, refusal posture) — `docs/ui/themes/modern-lcd.md`
- ADR-0008 (MudBlazor strict for chrome), ADR-0026 §6 (custom components for delight surfaces only)

## Governed by the frontend-blazor standard

The enforceable invariants of this design system are machine-checked rules in the
[`frontend-blazor` standard](../../../.claude/standards/frontend-blazor/STANDARD.md), run by
`/standards-audit`:

- **`FE-07` palette-pinned-modern-lcd** — the closed five-accent palette stays pinned to spec
  (`PinballThemeContractTests`); adding a sixth accent requires deleting one.
- **`FE-08` theme-design-system-sync** — a change to the implemented theme (`PinballTheme.cs` /
  `app.css :root`) must re-sync `tokens.css` here in the same PR (this directory is the mirror).
- **`FE-09` citation-as-hero-and-cta-parity** — citation cards stay full-fidelity/uncollapsed and
  peer outbound CTAs stay visually identical (the visual expression of provenance + no-favoritism).

`tokens.css` here re-states the default dark theme's tokens so the preview cards render standalone.

## Cards

**Foundations** — Surfaces & Ink · Semantic Accents (closed set) · Theme Variants (dark/light/neon) · Spacing & Panel Grammar
**Typography** — Display (Barlow Condensed) · Body (Inter) · Mono (JetBrains Mono)
**Components** — Buttons · Flipper-button CTA pair · Citation card (hero) · Inline citation marker · Refusal panel · Brand header & question input

## The two ideas that make it PinballWizard, not "another dark theme with orange buttons"

1. **Citation-as-hero.** Every answer ends with a stack of full-fidelity citation cards — the loudest
   objects on the page after the answer body. They make Provenance-is-sacred *visible*. Never collapsed.
2. **Community-resource posture.** The Wizard routes traffic *out* to source and community sites; it
   does not capture users. Outbound is a feature, refusal directs out, and peer destinations get
   visually identical CTAs (avoid any appearance of favoritism). A short session is a successful session.

The palette is **closed**: five semantic roles (amber primary, green grounded, red refusal, magenta
mode, plus the warm-near-black surface family), each paired with a non-color signal. Adding a sixth
accent requires deleting one.

## Syncing to claude.ai/design

This directory is the source for the **"PinballWizard Design System"** project on
[claude.ai/design](https://claude.ai/design) (distinct from the *Early Bird Solutions* marketing-site
design system). Each `preview/*.html` carries a first-line `<!-- @dsCard group="…" name="…" -->`
marker; the Design System pane builds its card index from those markers automatically — no manual
registration. To re-sync after editing, run the design sync against this folder as the local source
directory and write back `tokens.css`, `README.md`, and `preview/*.html`.

When the implemented theme changes (`PinballTheme.cs` / `app.css` `:root`), update `tokens.css` here
to match and re-sync — the repo is the source of truth; the claude.ai/design project is the mirror.
