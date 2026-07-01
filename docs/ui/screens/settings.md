# Settings Screen — Spec v1

> **Status:** v1 spec on `Dev-WebUiBrainstormResume`. Operationalizes the theme picker (and the small set of other locally-persisted preferences) into a dedicated screen. Blocking dependency for Wave 3 sibling-theme rollout.

## Purpose

Settings is the home for **locally-persisted user preferences** — the small set of choices the user makes about how the Wizard renders for them. It is *not* an account screen, *not* a profile, *not* a notifications hub. The community-resource posture explicitly forbids those surfaces ([ADR-0027](../../adr/0027-community-resource-posture.md) § 1, § 10), and the Wizard has no per-user account in v1 anyway ([ADR-0009](../../adr/0009-entra-external-id-admin-rbac-v1.md) — admin RBAC only; no end-user passport in v1).

What Settings covers in v1:

1. **Theme picker** — the only Wave 3 obligation that *requires* a Settings screen to ship. When sibling themes ship, users need somewhere to switch.
2. **Motion preference** — surface the `prefers-reduced-motion` media query as a user-overridable preference. Useful for users on devices that don't expose the OS-level setting clearly, or for users who want motion-on for specific themes.
3. **Sound preference** — the muted-by-default toggle from [ADR-0026](../../adr/0026-user-delight-frontend-and-streaming.md) § Explicitly NOT adopted. Persisted to localStorage; defaults to muted.

What Settings is explicitly **not** in v1:

- No "saved questions" list (engagement-metric framing forbidden per [ADR-0027](../../adr/0027-community-resource-posture.md) § 1).
- No "notification preferences" (no captive notifications surface in v1).
- No account / profile / sign-in (the Wizard is anonymous-by-construction for the public surface; admin auth lives in `AdminLayout`, not here).
- No data-export / data-import buttons (no per-user data exists to export).
- No "personalization" surfaces (no per-user behavior tracking — see ADR-0027 § 10).

This spec consumes:
- [`docs/ui/themes/modern-lcd.md`](../themes/modern-lcd.md) — locked default theme + visual tokens
- [`docs/ui/themes/sibling-themes-overview.md`](../themes/sibling-themes-overview.md) — sibling theme directional sketches + theme-picker UI considerations
- [`docs/ui/screens/answer-with-citations.md`](answer-with-citations.md) — locked visual tokens (color, type, spacing, motion) inherited verbatim
- [`docs/ui/prototypes/theme-picker.html`](../prototypes/theme-picker.html) — the working theme-picker prototype that established switching by CSS-variable swap + localStorage persistence
- [ADR-0026](../../adr/0026-user-delight-frontend-and-streaming.md) — Wave 1/2/3 implementation track
- [ADR-0027](../../adr/0027-community-resource-posture.md) — community-resource posture (the why-not list above derives from this ADR's § 10)

It does NOT cover: the answer screen, the empty/landing screen, the what-we-cover screen, the machine-detail screen, the `/error` page, the `/admin/*` surfaces.

## Routing

- **URL:** `/settings`
- **Access:** anonymous (no auth required) — it's a local-preferences surface, not an account-bound surface.
- **Layout:** `MainLayout` (the public chrome). Same header/footer as every other anonymous screen.
- **Container:** standard `WizardShell` centered container. No special width treatment.

## Information architecture

The page presents **three sections in vertical stack**, in this order:

| Order | Section | Why this order |
|---|---|---|
| 1 | **Theme** | The Wave-3-blocking surface; primary reason a user navigates here. |
| 2 | **Motion** | Adjacent to theme — both shape "how does the page feel to me?" |
| 3 | **Sound** | Smaller, less-frequently-touched preference. Mute-by-default per ADR-0026, so most users never come here unless they specifically want sound on. |

Each section is self-contained, identical card grammar, peer-parity per [ADR-0027](../../adr/0027-community-resource-posture.md) § 2 (no "primary" section visually elevated above its peers). Section card grammar matches the citation card from `answer-with-citations.md` § State 2: `--bg-surface` background, `--border-quiet` border, `--space-3` internal padding, `--radius-panel` radius.

## Per-section spec

### Section 1 — Theme

**Section header** — `--font-display` 700 `--type-lg` `--text-primary` ALL CAPS: `THEME`.

**Section subtext** — `--font-body` `--type-sm` `--text-secondary`:

> Choose how the Wizard looks. Your selection is remembered on this device.

**Theme picker grid** — sibling themes rendered as labeled previews per [`sibling-themes-overview.md`](../themes/sibling-themes-overview.md) § Theme picker — UI consideration. Each preview is a card with:

- **Theme name** (`--font-display` 700 `--type-md`).
- **Aesthetic one-liner** (`--font-body` `--type-sm` `--text-secondary`) — the "Era / Mood" line from the sibling sketch.
- **Visual swatch** — a small preview of the theme's palette: 3 color chips (background, primary accent, grounded accent) + a one-line type sample in the theme's display font. Swatch is ~80×80px on desktop, ~64×64px on mobile.
- **`BETA` tag** — for any sibling theme that hasn't earned a full spec yet (per the sibling-themes-overview guidance). Default-Modern-LCD has no tag. Cabinet, Score Reel, and any future siblings carry `BETA` until each earns its own `docs/ui/themes/[name].md`.
- **Selection affordance** — a single `<input type="radio" name="theme">` per card; the whole card is the click target via `<label>`. The selected card carries a `--border-glow-grounded` border treatment to indicate the active state.

**Card grammar (peer-parity rule):** every theme preview card is **visually identical in structure** — same dimensions, same internal layout, same swatch size, same type weight on the name. Per [ADR-0027](../../adr/0027-community-resource-posture.md) § 2: no "default" card visually elevated, no "recommended" badge on Modern LCD beyond the absence of a `BETA` tag. The signal that Modern LCD is the default for new users is *the absence of `BETA`*, not a "DEFAULT" badge that would read as endorsement.

**Layout:**

- **Mobile (< 768px):** single-column stack. One theme per row.
- **Desktop (≥ 768px):** 2-column or 3-column grid (auto-fit `minmax(280px, 1fr)`). The grid stays uniform — no theme is featured wider or taller than its siblings.

**Ordering:** alphabetical by theme name (resolver-computed at render time, not baked into the markup), matching the [ADR-0027](../../adr/0027-community-resource-posture.md) § 3 within-set ordering rule. Adding a new sibling theme later doesn't require re-ordering the grid manually. This applies even to Modern LCD — it appears alphabetically, not first by editorial choice.

**Persistence:** the theme selection persists to `localStorage` under the key `pinwiz.theme` (matching the working prototype at [`docs/ui/prototypes/theme-picker.html`](../prototypes/theme-picker.html)). On page load, `MainLayout` reads the key and sets a `data-theme="<name>"` attribute on `<html>`; CSS variable swaps activate the theme. Default for any user without a `pinwiz.theme` value: Modern LCD.

**Accessibility:** the picker is a `<fieldset>` with a `<legend>` reading "Theme"; each option is a labeled `<input type="radio">`. The `BETA` tag is announced via `aria-label="<theme name>, beta — has not yet earned a full design specification"`. Active card carries `aria-checked="true"` (via the radio input's checked state).

### Section 2 — Motion

**Section header** — `--font-display` 700 `--type-lg` `--text-primary` ALL CAPS: `MOTION`.

**Section subtext** — `--font-body` `--type-sm` `--text-secondary`:

> The Wizard uses subtle motion (panel reveals, citation glow, hover transitions) to signal state. You can override your device's setting.

**Three-option radio group** (NOT a binary toggle — three states is honest):

| Option | Behavior | When this is right |
|---|---|---|
| **Match my device** (default) | Honors the OS-level `prefers-reduced-motion` query. | Most users. The browser's setting is the source of truth. |
| **Always on** | Forces motion on regardless of OS setting. | Users who want the full visual experience even on devices where the OS reports reduced-motion (e.g., a battery-saving mode that secondarily reports reduced-motion but the user wants full motion in this app). |
| **Always off** | Forces motion off regardless of OS setting. | Users sensitive to motion who want the strictest setting in this app specifically. |

**Implementation:** the selection persists to `localStorage` under `pinwiz.motion` (values: `"match"`, `"on"`, `"off"`; default `"match"`). On page load, `MainLayout` reads the key and applies a `data-motion="<value>"` attribute on `<html>`; CSS rules conditionally honor or override the `prefers-reduced-motion` media query based on this attribute. Per the locked motion-reduced fallback rule in `answer-with-citations.md` § Accessibility, the `match` and `off` states both result in all `--motion-*` durations becoming `0ms`.

**Accessibility:** `<fieldset>` + `<legend>` "Motion preference"; each option is a `<input type="radio">` with a clear `<label>`. The currently-active selection carries `aria-checked="true"`.

### Section 3 — Sound

**Section header** — `--font-display` 700 `--type-lg` `--text-primary` ALL CAPS: `SOUND`.

**Section subtext** — `--font-body` `--type-sm` `--text-secondary`:

> The Wizard is muted by default. If sounds are added in a future release (subtle pinball-themed callouts on answer reveal), this toggle controls whether they play.

**Single binary toggle:**

| State | Behavior |
|---|---|
| **Muted** (default) | All sound assets remain unloaded; no audio plays. |
| **Sound on** | Future sound assets play (subject to ADR-0026 § Explicitly NOT adopted: never auto-play in any state — sounds only fire as response to user interaction, never on page load). |

**Implementation:** persists to `localStorage` under `pinwiz.sound` (values: `"muted"`, `"on"`; default `"muted"`). The `SoundController` component (referenced in [ADR-0026](../../adr/0026-user-delight-frontend-and-streaming.md) § 6) reads this key and exposes the toggle state to any component that emits sound. The toggle is the *only* way to turn sound on — the showcase posture forbids auto-playing audio in any flow ([ADR-0026](../../adr/0026-user-delight-frontend-and-streaming.md) § Explicitly NOT adopted).

**v1 caveat:** v1 ships with no sound assets. The toggle exists in v1 so the persistence layer is in place when sound assets ship later, and so the user surface signals the project's posture (sound is opt-in, never auto-on) from day one.

**Accessibility:** a single labeled checkbox or `<input type="checkbox" role="switch">` with a clear `<label>`.

## Per-state variants

Settings is mostly a single state. Two nuances:

### State — Theme just changed

Briefly (200ms) dim the page during the CSS-variable swap so the transition reads as deliberate rather than as a flicker. The dim uses `--motion-medium` (240ms) and is suppressed entirely under `prefers-reduced-motion: reduce` or with `pinwiz.motion = "off"`.

### State — localStorage unavailable

If `localStorage` is unavailable (e.g., user has disabled it for privacy), the page renders normally but a small `--text-secondary` `--type-xs` caption appears above the theme grid:

> Browser storage is disabled. Your selection won't persist between visits.

Each control still works for the current page-load. No errors thrown; no banner.

## Mobile vs desktop

### Mobile (< 768px)

- All three sections stack vertically, full-width.
- Theme grid: single-column.
- Motion radios: stacked vertically (one option per row).
- Sound toggle: inline with its label.
- Section spacing: `--space-4` between sections.

### Desktop (≥ 768px)

- All three sections stack vertically (no two-column layout — preferences are sequential, not parallel).
- Theme grid: 2-column or 3-column auto-fit grid.
- Motion radios: horizontal row (3 options side-by-side).
- Sound toggle: inline with its label.
- Section spacing: `--space-5` between sections.

## Interaction details

### On page load

- Read `pinwiz.theme`, `pinwiz.motion`, `pinwiz.sound` from `localStorage`.
- Apply theme via `<html data-theme="...">` (matches existing prototype).
- Apply motion preference via `<html data-motion="...">`.
- Apply sound preference via the `SoundController` component's initial state.
- Render each section with the active selection highlighted.

### Click on a theme card

- Select the radio.
- Apply `<html data-theme="<name>">` immediately.
- Persist to `localStorage`.
- The 200ms dim transition fires (suppressed under reduced-motion).
- No page reload.
- No confirmation toast — the visual change *is* the confirmation. Per [ADR-0027](../../adr/0027-community-resource-posture.md) § 10, no engagement-metric / "saved!" notification framing.

### Click a motion radio

- Select the radio.
- Apply `<html data-motion="<value>">` immediately.
- Persist to `localStorage`.
- No page reload, no confirmation toast.

### Click the sound toggle

- Toggle the state.
- Persist to `localStorage`.
- No page reload, no confirmation toast.
- v1: no audible feedback (no assets exist yet). When sound assets ship, the toggle's `on` state may emit a single brief "click" sound on toggle-on as confirmation — never on toggle-off, never on page-load.

## Accessibility

- Each section is a `<section>` with an `<h2>` header.
- Each control group is a `<fieldset>` with a `<legend>`.
- All radio / checkbox inputs have visible `<label>`s.
- Focus order: theme grid first, then motion radios, then sound toggle. Within the theme grid, alphabetical order matches visual order (no tab-order surprises).
- Keyboard nav: `Tab` moves between fieldsets; `Arrow` keys navigate within a radio group; `Space` selects.
- All visual state changes (theme switch, motion toggle, sound toggle) respect `prefers-reduced-motion: reduce` (and the user's overriding `pinwiz.motion` preference).
- Color contrast: every theme's foreground/background pairs verified at WCAG AA (per the locked rule in `modern-lcd.md` § Visual system, palette rule "Every accent ↔ background combination passes WCAG AA"). The theme picker's selected-card border uses the active theme's `--border-glow-grounded` so the affordance is visible regardless of theme.

## Out of scope for this spec

- **Account / profile / sign-in.** v1 is anonymous on the public surface. If end-user accounts ship later (Entra External ID for end-user social login per [`memory/project_phase2_architecture_decisions.md`](../../../../Users/JimKeeley/.claude/projects/C--projects-PinballWizard/memory/project_phase2_architecture_decisions.md) — when passport ships), an account section *may* be added here, but it lives in a separate Settings v2 spec PR.
- **Saved questions / history.** Forbidden in v1 per [ADR-0027](../../adr/0027-community-resource-posture.md) § 10 (engagement-metric framing). The shareable-deep-link pattern (`/wizard/q/{slug}` per [ADR-0026](../../adr/0026-user-delight-frontend-and-streaming.md) § 1) covers the "I want to come back to this answer" use case without state on the client.
- **Notifications preferences.** No notifications in v1. If notifications ever ship for genuinely useful reasons (a manufacturer publishes a service bulletin for a machine the user explicitly subscribed to), they go through a dedicated subscriptions surface, not a generic notifications-prefs surface.
- **Data export / data import.** No per-user data exists in v1 to export. If account-bound data exists later, this is a separate Settings v2 surface.
- **"Reset to defaults" button.** Not in v1 — defaults are recoverable by clearing browser storage, which is the standard browser-mediated reset path. A dedicated reset button would imply user-data complexity that doesn't exist.
- **Per-theme palette overrides** (e.g., "use Modern LCD with this accent color"). Themes are atomic units; adding palette-override controls re-litigates the locked-palette rule in [ADR-0026](../../adr/0026-user-delight-frontend-and-streaming.md) § 6. If a theme's palette feels wrong for a user, the right fix is a sibling theme that addresses the gap, landed via the sibling-theme-promotion path.
- **Language / locale.** v1 is English-only. Localization spec lives in a separate doc when it earns its keep.
- **Admin settings.** Admin RBAC + admin-side preferences (if any) live in `AdminLayout` per [ADR-0009](../../adr/0009-entra-external-id-admin-rbac-v1.md). The public Settings screen never surfaces admin controls.

## Iteration log

| Date | Change | Rationale |
|---|---|---|
| 2026-05-09 | v1 spec | Operationalizes the theme picker into a dedicated screen (the Wave 3 obligation that unblocks sibling-theme rollout per [ADR-0026](../../adr/0026-user-delight-frontend-and-streaming.md)). Three sections in v1: theme, motion, sound — peer parity, alphabetical theme ordering, no "default"/"recommended" badges that would re-litigate [ADR-0027](../../adr/0027-community-resource-posture.md) § 2. Explicit non-scope for account/saved-questions/notifications/data-export per [ADR-0027](../../adr/0027-community-resource-posture.md) § 10 (no captive UI, no engagement-metric framing). Persistence via localStorage matching the working prototype at [`docs/ui/prototypes/theme-picker.html`](../prototypes/theme-picker.html). Three-state motion preference (match-device / on / off) instead of a binary toggle — three states is honest about what the OS-level query actually expresses. Sound toggle is mute-by-default per [ADR-0026](../../adr/0026-user-delight-frontend-and-streaming.md) § Explicitly NOT adopted (auto-playing audio rejected permanently); v1 ships the toggle even though no sound assets exist yet, so persistence is in place when assets land later and the user-surface signal of "sound is opt-in, never auto-on" is established from day one. |
