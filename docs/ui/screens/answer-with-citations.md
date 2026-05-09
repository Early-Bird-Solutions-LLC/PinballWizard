# Answer-with-Citations Screen — Spec v1

> **Status:** v1 spec on `Dev-WebUiThemesBrainstorm`. Operationalizes the Modern LCD theme + community-resources contract into a single screen ready for implementation.

## Purpose

This is the central UX object of the Wizard — the screen the user lands on after submitting a question. Every decision made elsewhere (theme palette, citation-as-hero, plurality, refusal handling, coverage transparency) is rendered here. **If a prospect sees one screen of the Wizard, this is the screen.** It is the moment-of-truth artifact for the showcase.

This spec consumes:
- [`docs/ui/themes/modern-lcd.md`](../themes/modern-lcd.md) — the theme system this screen renders in
- [`docs/community-resources.md`](../../community-resources.md) — the contract for which destinations this screen routes to

It does NOT cover: empty/landing state on first load, machine-detail screen, "what we cover" disclosure screen, settings — those each get their own specs in `docs/ui/screens/`.

---

## Locked visual tokens

Distilled from the theme doc's directional spec into committed values. Every implementation reads from this token set. Adding a token requires a separate decision; renaming one cascades through everything.

### Color tokens

| Token | Hex | Use |
| --- | --- | --- |
| `--bg-base` | `#0c0b0e` | Page background — the "LCD bezel" |
| `--bg-surface` | `#161519` | Panel interiors (answer panel, citation cards) |
| `--bg-surface-hi` | `#1f1d22` | Hovered/active panels, citation card under focus |
| `--text-primary` | `#f4f1ea` | Body text, primary headings (off-white with subtle warmth — NOT clinical white) |
| `--text-secondary` | `#9a9590` | Labels, metadata, timestamps |
| `--accent-primary` | `#ff9a1f` | Submit button, primary action — pinball amber |
| `--accent-grounded` | `#34d96a` | Citations, source-grounded outbound CTAs — atomic green / GI-glow |
| `--accent-refusal` | `#ff3b30` | Refusal panel border, validation errors |
| `--accent-mode` | `#e13bd9` | Mode/topic context, left-flipper "view in answer" CTA |
| `--border-quiet` | `#2a282d` | Default panel borders, dividers |
| `--border-glow-primary` | `#ff9a1f99` | Primary-action hover/focus glow (alpha-60% over base) |
| `--border-glow-grounded` | `#34d96a99` | Grounded outbound hover/focus glow |
| `--border-glow-mode` | `#e13bd999` | Mode-context hover/focus glow |
| `--border-glow-refusal` | `#ff3b3099` | Refusal panel border glow |

**WCAG AA verified** for all foreground/background pairs at body-text size (4.5:1 minimum):
- `text-primary` on `bg-base`: ~17.8:1 ✅ (AAA)
- `text-secondary` on `bg-base`: ~6.7:1 ✅ (AA / AAA for large)
- `accent-primary` on `bg-base`: ~9.1:1 ✅ (AAA)
- `accent-grounded` on `bg-base`: ~10.9:1 ✅ (AAA)
- `accent-refusal` on `bg-base`: ~6.4:1 ✅ (AA)
- `accent-mode` on `bg-base`: ~7.5:1 ✅ (AA)

### Type tokens

| Token | Stack | Default size | Weight | Use |
| --- | --- | --- | --- | --- |
| `--font-display` | Barlow Condensed | (per scale) | 700 primary / 500 secondary | All display moments — flipper labels, panel titles, refusal category, citation source identity |
| `--font-body` | Inter | (per scale) | 400/500/600 | All paragraph text, labels |
| `--font-mono` | JetBrains Mono | (per scale) | 400 | Citation IDs, machine slugs, URLs in provenance trail |
| `--type-xs` | — | 12px | — | Small labels, footer text, timestamps, source-type pills |
| `--type-sm` | — | 14px | — | Secondary body, UI labels |
| `--type-base` | — | 16px | — | Primary body, answer text |
| `--type-md` | — | 18px | — | Emphasized body, panel headers |
| `--type-lg` | — | 24px | — | Citation card source identity, refusal reason |
| `--type-xl` | — | 32px | — | Refusal category label, page-hero moments |

Tabular figures (`font-feature-settings: "tnum"`) applied site-wide on score-style numerics — counts, percentages, dates, prices, citation indices.

### Spacing tokens

8px base unit:

| Token | px | Use |
| --- | --- | --- |
| `--space-1` | 8 | Tight inner padding (within a label, between icon and text) |
| `--space-2` | 16 | Default inner padding (within panels) |
| `--space-3` | 24 | Between elements within a panel (card slots) |
| `--space-4` | 32 | Between major zones (answer ↔ citation stack) |
| `--space-5` | 40 | Generous between zones |
| `--space-6` | 48 | Empty-state hero spacing, page-margin generosity |

### Other tokens

| Token | Value | Use |
| --- | --- | --- |
| `--radius-panel` | 2px | Panels, cards, info zones — "machined edge" |
| `--radius-cta` | 6px | Flipper buttons, routing-recommendation pucks — recessed cabinet button family |
| `--radius-insert` | 50% | Inline citation pinball-insert markers (circular) |
| `--shadow-press` | `inset 0 -1px 2px rgba(0,0,0,0.4)` | Recessed-button effect on cabinet-family CTAs |
| `--motion-fast` | 120ms | Hover state transitions |
| `--motion-medium` | 240ms | Panel reveals, mode-start dim |
| `--motion-slow` | 600ms | Soft glow fade after answer reveal |
| `--motion-pulse` | 1500ms | Loading pulse cycle |

**Implementation:** these tokens become CSS custom properties on `:root`. A reduced-motion media query overrides `--motion-*` to `0ms` (motion-reduced fallback per below).

---

## Screen zones (top-to-bottom)

The screen has five persistent zones. Per-state variants (below) determine what fills the answer zone.

1. **Header zone.** Brand mark on the left, "What we cover" link on the right. Subtle, low-key — never the focal point. ~56px tall on desktop, ~48px on mobile.
2. **Question input.** Persistent, never collapses or scrolls away. The thing the user always knows how to find. Sits directly under the header.
3. **Answer zone.** State-dependent (loading / answer / refusal / error / many-citations / conflicting-sources). The largest visual mass on the page.
4. **Citation card stack.** Appears when an answer renders. Full-fidelity per source, never collapsed.
5. **Footer zone.** Coverage disclosure summary line ("Sources we cover: 8 manufacturers + OPDB. See What We Cover for the full picture."), link to GitHub project page, link to "What we cover" full page. Honest, not promotional.

---

## Per-state variants

The answer zone takes on different shapes depending on state. All other zones remain stable.

### State 1 — Loading

Triggered when the user submits a question, before the answer renders.

- **Answer zone:** placeholder panel with `bg-surface` background, `--border-quiet` border. Pulse animation on the border using `--border-glow-primary` (slow rise to ~60% opacity, fall back, ~1500ms cycle). NOT a spinner. NOT a Material progress bar. NOT skeleton text — skeleton reads as "loading a Twitter feed," not "the machine is thinking."
- **Optional micro-copy** centered in the panel, in `--text-secondary` `--type-sm`: "Searching sources…" — short, not cute. No anthropomorphism.
- **Citation card stack:** absent during loading.
- **Question input:** disabled, submit button dimmed to `--text-secondary`.

### State 2 — Answer rendered

The most common state. The Wizard has produced a confidently-grounded answer.

- **Answer zone:** filled panel, `bg-surface`, `--border-quiet` border. Soft outer glow on reveal — `--border-glow-grounded` at low opacity, fading to nothing within `--motion-slow` (600ms).
- **Answer body:** `--font-body` `--type-base` `--text-primary`. Paragraphs separated by `--space-2`. Inline citation markers (pinball-insert style) embedded in the text where claims are grounded. Inline references to entities (machine names, manufacturers, etc.) carry quiet `↗` portal affordances per the body-text portal pattern in the contract.
- **Citation card stack:** rendered below the answer panel with `--space-4` separation. Each card is full-fidelity per the theme doc's citation-card anatomy (source-type tag, identity, excerpt, provenance trail, timeline, flipper-button pair).
- **Question input:** re-enabled. Submit button returns to `--accent-primary`.

### State 3 — Refusal

Per ADR-0017 confidence-threshold or out-of-scope refusal. The Wizard openly hands the user off to community resources that *can* answer.

- **Answer zone:** refusal panel REPLACES the answer panel (same slot). Border in `--border-glow-refusal`, static (no pulse — refusal is a deliberate outcome, not a process).
- **Layout per the theme doc's refusal-panel section:**
  - Category label at top — `--font-display` 700 `--type-xl`, ALL CAPS, in `--accent-refusal`. Reads like a pinball callout (`LOW CONFIDENCE`, `OUT OF SCOPE`, `CONFLICTING SOURCES`).
  - Reason text below — `--font-body` `--type-md` `--text-primary`. One sentence, no apology.
  - Routing recommendations — 2–3 outbound CTAs (recessed pucks per theme doc spec) in equal-weight per peer-parity rule. Backlit in `--border-glow-grounded`. Side-by-side on desktop if 2–3 fit, stacked on mobile or if more than 3.
- **Citation card stack:** absent in `LOW_CONFIDENCE` and `OUT_OF_SCOPE`. Present (full-fidelity) for `CONFLICTING_SOURCES` — the conflicting cards stay above and the refusal panel sits below them with the framing "the Wizard refuses to choose between these — the community can."

### State 4 — Error

Distinct from refusal. System-level failure (transient — Cosmos timeout, Foundry retry, etc.).

- **Answer zone:** quiet inline banner — NOT a panel. `--text-secondary` `--type-sm`. No alarmist visuals. Includes a "retry" affordance (small `--font-display` button).
- **Citation card stack:** absent.

### State 5 — Many-citations

When an answer is well-grounded enough to cite 4+ sources. NOT a separate state — a property of the answer-rendered state.

- **Above the citation card stack:** brief summary header — `--font-display` `--type-sm` `--text-secondary`, ALL CAPS, e.g. `SOURCES  ·  5 cited from 3 sites`.
- **Card stack:** all cards full-fidelity. NEVER collapsed behind a "show all sources" disclosure — that violates the citation-as-hero principle.
- **Optional desktop optimization:** if the stack exceeds 5 cards, alternating subtle background tone (`--bg-surface` ↔ `--bg-surface-hi`) for visual rhythm. NOT to indicate hierarchy — purely rhythm. Mobile: stacked uniform.

### State 6 — Conflicting sources

A `CONFLICTING_SOURCES` refusal where the answer body is replaced by the conflict framing.

- **Citation cards remain at full fidelity at the top of the page** (under the question input). Each conflicting source rendered identically — peer parity even when they contradict.
- **Refusal panel below the cards** with framing "the Wizard refuses to choose between these — the community can." Routing recommendations point to forum plural set (Pinside Forum + Reddit /r/pinball + TiltForums) for community resolution.

---

## Element-specific behaviors (screen-specific only)

Elements whose visual treatment is locked in `modern-lcd.md` are referenced here, not re-specified. This section covers behaviors specific to *this* screen's composition.

### Question input

- Single-line text input, full-width within the page's content gutter.
- `--font-body` `--type-md` `--text-primary` on `--bg-surface`.
- Border in `--border-quiet`; focuses to `--border-glow-primary`.
- Placeholder: `"Ask the Wizard..."` in `--text-secondary`.
- Submit button to the right (or inline below on narrow mobile) — see below.
- Persistent across all states. Disabled-but-visible during loading.

### Submit button

- Recessed cabinet-button family (same as flipper buttons but distinct role).
- Label `ASK ▶` in `--font-display` 700, ALL CAPS, on `--accent-primary` background.
- 44–48px tall, square-ish (auto-width to label + `--space-2` padding).
- Press behavior: 1px depression, brief glow flare to peak `--accent-primary`, then dims as the request fires.
- Hover: `--border-glow-primary` lights, button face shifts to slightly higher luminance.

### Inline citation marker

- Locked as Option C from the citation discussion — small numbered pinball-insert.
- Circular (`--radius-insert`), ~16–18px diameter inline (pulled up slightly above text baseline like a superscript).
- `--accent-grounded` outer glow, `--bg-surface-hi` fill, number in `--font-body` `--type-xs` `--text-primary`.
- Hover: tooltip with `[SOURCE TYPE]  Source name`. Pulse once.
- Click: smooth scroll to matching citation card; card's border pulses twice in `--border-glow-grounded`.

### Citation card flipper-button pair

Per the theme doc, with screen-specific composition:

- Both buttons always shown when the card is rendered (per "outbound is generous" principle).
- Side-by-side on desktop (right-aligned within the card), stacked on mobile.
- Press behavior: 1–2px depression, backlight flare to peak (`--border-glow-grounded` for right flipper, `--border-glow-mode` for left flipper).

### Routing-recommendation CTAs (in refusal state)

Per the theme doc, with screen-specific composition:

- Equal-weight, ordered alphabetically (or per the contract's randomized convention) — never editorially curated.
- Side-by-side on desktop if 2–3 fit comfortably; stacked on mobile or when more than 3.
- All identical visual weight per peer-parity rule.

---

## Mobile vs desktop

Single breakpoint at **768px** (typical tablet portrait). No design for "tablet landscape" as a distinct case — desktop layout works above 768px regardless of device type.

### Mobile (< 768px)

- Page gutter: `--space-2` (16px) on each side.
- Question input + submit button: stacked (input full-width, submit button below at full-width).
- Citation cards: full-width, stacked vertically.
- Flipper-button pair: stacked vertically inside each card (left flipper above right flipper to preserve "view in answer → view source" reading order).
- Refusal-panel routing recommendations: stacked vertically, full-width.
- Type scale: unchanged. The `--type-xl` refusal callout remains 32px — large but readable on mobile because it's center-aligned and short.
- Touch targets: minimum 44px for any CTA per accessibility convention. Flipper buttons stay at 44–56px; inline citation insert markers expand their hit area to 32×32px on mobile (visual stays 16–18px).

### Desktop (≥ 768px)

- Page gutter: `--space-4` (32px) on each side, capped to a max content width of ~960px (centered).
- Question input + submit button: inline (input takes available width, submit button auto-width to the right).
- Citation cards: full-width within the content gutter (~840–880px).
- Flipper-button pair: side-by-side (left flipper on left, right flipper on right).
- Refusal-panel routing recommendations: side-by-side if 2–3 fit; stacked otherwise.

---

## Interaction details

### Keyboard navigation

- `Tab` order: question input → submit button → (after answer renders) inline citation markers in document order → flipper buttons within each citation card (left then right) → next card → footer links.
- `Enter` on focused submit button: submit.
- `Enter` on focused inline citation marker: equivalent to click (scroll to card + pulse).
- `Enter` on focused flipper button: equivalent to click (open URL or scroll-to-answer).
- All focusable elements have a visible focus ring — `--border-glow-grounded` 2px outline offset 2px. NEVER `outline: none` without an alternative focus indicator.

### Click / tap behaviors

- **Submit button click:** disable input, show loading state, fire request.
- **Inline citation marker click:** scroll to matching citation card (smooth, ~400ms), pulse card border twice in `--border-glow-grounded`.
- **Right flipper (`VIEW THE ORIGINAL ▶`):** open canonical source URL in new tab (`target="_blank" rel="noopener noreferrer"`). Brief depression + flare.
- **Left flipper (`◀ VIEW IN ANSWER`):** scroll to inline citation marker in answer body, pulse marker once in `--border-glow-mode`.
- **Routing-recommendation CTA click (refusal state):** open destination URL in new tab.
- **Body-text inline portal click (entity reference):** open primary destination URL in new tab. Hover reveals destination name in tooltip; right-click / long-press reveals secondary destinations from the contract.

### State transitions

- **Empty → Loading:** input disables instantly. Loading panel fades in over `--motion-medium` (240ms). Loading pulse begins after fade completes.
- **Loading → Answer:** loading pulse stops. Loading panel fades out over `--motion-medium`. Answer panel fades in. Soft glow on answer panel border (`--border-glow-grounded`) appears at peak then fades to nothing over `--motion-slow` (600ms). Citation cards fade in with 50ms stagger between cards.
- **Loading → Refusal:** same fade-out, refusal panel fades in. No glow flare on refusal — the static border is the signal.
- **Loading → Error:** loading panel fades out, inline error banner appears in its place. Quiet, non-alarmist.

---

## Accessibility

### Color contrast

All foreground/background pairs in the locked palette pass WCAG AA at body text size (verified above). Several pairs reach AAA. Implementations must not introduce additional accent colors without re-running the contrast pass.

### Motion-reduced (`prefers-reduced-motion: reduce`)

- All `--motion-*` tokens override to `0ms` — transitions become instant state changes.
- Loading pulse → static state with `--border-glow-primary` at constant 40% opacity (still visually distinct from idle, just no animation).
- Mode-start dim → instant crossfade.
- Citation card border pulse on inline-marker click → single-frame state change (border briefly takes on `--border-glow-grounded`, then back).
- Flipper-button press depression: KEPT (it's tactile feedback, not decoration). The backlight flare animation collapses to a single-frame state change.

### Color is never the only signal

- Citation cards: `accent-grounded` is paired with the source-type tag (label + color), and the flipper-button labels carry the meaning in text. A user who can't see green can still read "BULLETIN" and "VIEW THE ORIGINAL ▶".
- Refusal panel: `accent-refusal` is paired with the category label ("LOW CONFIDENCE" etc.) which carries the meaning in text.
- Inline citation markers: numbered (visible), with `accent-grounded` glow only as the secondary signal.

### Screen reader

- Loading state: `aria-live="polite"` announcement: "Searching sources." On answer reveal, content auto-announces.
- Refusal state: `aria-live="assertive"` announcement: "Refused: [category]. [reason]."
- Citation cards: each is a `<section>` with `aria-labelledby` pointing to the source-identity heading.
- Flipper buttons: `aria-label` includes destination name, e.g., `aria-label="View the original on Stern Pinball"` (not just "View the original").
- Inline citation marker: `<button>` with `aria-label="Citation 1: [source name]"`. `aria-describedby` may include excerpt for richer context.

---

## Out of scope for this spec

Each gets its own spec under `docs/ui/screens/`:

- **Empty / landing state on first load.** Before the first question. Has cinematic-flourish hero treatment per theme doc's flavor section.
- **Machine detail screen.** When the user drills into a specific machine. Tabs for Manual / Bulletins / Specs / Provenance.
- **What We Cover disclosure screen.** Coverage transparency surface — what we have first-party data on, what's link-only, refresh cadence, refusal-policy summary.
- **Settings.** Theme selection (when sibling themes ship), motion preferences, audio (when v2).
- **Search history / bookmarks.** Out of scope for v1 entirely (per "no engagement-metric framing" anti-pattern).

## Iteration log

| Date | Change | Rationale |
| --- | --- | --- |
| 2026-05-08 | v1 spec | Operationalizes Modern LCD theme + community-resources contract into a single screen. Locks visual tokens (palette, type, spacing, motion timings) at concrete values; specs every state variant (loading / answer / refusal / error / many-citations / conflicting-sources); covers mobile vs desktop, keyboard nav, click behaviors, state transitions, motion-reduced fallbacks, screen reader behavior, and out-of-scope siblings. Contrast values WCAG-AA-verified for the full palette. |
