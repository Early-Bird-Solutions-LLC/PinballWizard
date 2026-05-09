# Empty / Landing Screen — Spec v1

> **Status:** v1 spec on `Dev-WebUiThemesBrainstorm`. Operationalizes the cinematic-flourish moment of Modern LCD into the cold-load screen — what every new user (and every prospect) sees before asking anything.

## Purpose

This is the **first impression** screen. Cold-load entry, no question asked yet, no answer to render. It serves three audiences:

1. **First-time users** — needs to communicate what the Wizard is in seconds.
2. **Prospects** evaluating the project as showcase material — the visual moment that establishes "this is enterprise-quality, not a hobby weekend project."
3. **Returning users** — needs to invite them to ask without forcing them through a modal or onboarding.

This is the one screen where **cinematic flourish** (per the Modern LCD flavor section — JJP Wonka / Godfather / Elvis aesthetic — layered, painterly, slow, premium) is the dominant register. App-native restraint and broadcast punch are still present, but cinematic leads here.

This spec consumes:
- [`docs/ui/themes/modern-lcd.md`](../themes/modern-lcd.md) — theme system
- [`docs/ui/screens/answer-with-citations.md`](answer-with-citations.md) — locked visual tokens (color, type, spacing, motion) inherited verbatim
- [`docs/community-resources.md`](../../community-resources.md) — the contract whose posture the screen quietly signals

It does NOT cover: post-submit answer screen, "what we cover" disclosure screen, machine-detail screen, settings, error states.

---

## Inherited tokens

All visual tokens from `answer-with-citations.md` carry through unchanged. **One new token introduced for this screen only:**

| Token | Value | Use |
| --- | --- | --- |
| `--type-2xl` | 48px (mobile) / 64px (desktop) | Empty-state hero moment ONLY. No other surface uses this size. |

If a future surface earns a `--type-2xl` use, it gets re-evaluated then. The token is empty-screen-only by design — hero scale is reserved for the moment where it earns its keep.

---

## Information architecture

The page communicates four things, in this order:

| Order | Section | Purpose |
| --- | --- | --- |
| 1 | **Hero** | What the Wizard is, in two-or-three lines. Cinematic moment. |
| 2 | **Question input** | The call to action. Central, prominent. |
| 3 | **Suggested-question helpers** | A short curated set of example questions demonstrating what the Wizard can do. Establishes range. |
| 4 | **Coverage summary + footer** | Brief honest "what we cover" line with link to the full disclosure. Then standard footer. |

Notably absent (per the no-engagement-metric posture):

- **No "trending questions"** or "popular searches"
- **No "sign up for updates"** before the user has experienced the route-out journey
- **No testimonials, no social proof, no metrics about question volume**
- **No tutorial / walkthrough**. The Wizard works the same way it always works — type a question, get a sourced answer.

---

## Screen zones (top-to-bottom)

Same five-zone composition as the answer screen:

1. **Header zone** — same as other screens (brand mark, "What we cover" link). Subtle on this screen — the hero takes the eye.
2. **Hero zone** — replaces the answer zone in the IA. Generous vertical space — at least `--space-6` above and below.
3. **Question input** — the persistent input, anchored just below the hero. Highlighted by larger size and brighter focus ring than on other screens — this IS the call to action.
4. **Suggested-question helpers** — sits below the question input with `--space-4` separation.
5. **Footer zone** — same as other screens. Includes the coverage-summary one-liner.

---

## Per-section spec

### Section 1 — Hero

The cinematic moment. A single arresting typographic statement of what the Wizard is.

**Composition (top to bottom within the hero zone):**

1. **Tagline** — a single line above the wordmark. `--font-body` `--type-sm` `--text-secondary`, ALL CAPS, letter-spaced. e.g.: `A COMMUNITY-RESOURCE PINBALL WIZARD`.
2. **Wordmark** — the project name as the hero element. `--font-display` 700 `--type-2xl` (48px mobile / 64px desktop) `--text-primary`. Lock the brand: **PINBALLWIZARD** (one word, all caps in the hero — full readable casing in the header brand mark).
3. **Subline** — the differentiator phrased plainly. `--font-body` `--type-md` `--text-primary`. **Locked copy:** *"Ask anything about pinball — every answer cites its source and routes you to the community."*
4. **Ambient pulse element** — a single subtle "lit-insert" detail near the wordmark. A small circle (~12px) in `--accent-grounded`, glowing with a slow pulse cycle (~3s, gentle). The one delight beat per the cinematic-flourish guidance — not a screensaver, just a single ambient signal that the screen is *alive*.

**Spacing:** `--space-6` (48px) above the tagline. `--space-2` between tagline and wordmark. `--space-3` between wordmark and subline. `--space-6` below the subline (before the question input).

**Background treatment:** subtle vertical gradient from `--bg-base` at top to `#0a090d` at bottom (very minor lift — suggests the curve of an LCD bezel reflecting room light). Implementation must respect `prefers-reduced-motion` for any future animation; the gradient itself is static.

**No imagery, no illustration.** The hero is purely typographic + the single ambient pulse element. This is the **mechanics-not-IP** principle made literal — no machine image, no manufacturer logo, no game art. The Wizard belongs to the whole hobby.

### Section 2 — Question input

Same anatomy as the persistent question input on other screens, with these emphasis adjustments for the empty/landing context:

- **Larger:** `--type-md` instead of `--type-base` for the input text.
- **Centered placeholder:** `"Ask the Wizard..."` in `--text-secondary`.
- **Brighter focus ring on first focus:** `--border-glow-primary` at 80% opacity (vs. ~60% on other screens). This is the call-to-action moment; the focus ring earns more saturation here.
- **Submit button:** same visual as elsewhere (`accent-primary` recessed-cabinet button, `ASK ▶` label).
- **Auto-focus on mount:** the input receives focus on initial page load (with respect to mobile keyboard-popping etiquette — see Mobile vs Desktop below).

### Section 3 — Suggested-question helpers

A short, curated set of example questions. NOT a "popular searches" surface — these are hand-picked examples demonstrating the Wizard's range across question topics and across the manufacturer parity (per appearance-of-favoritism principle).

**Section sub-text** in `--text-secondary` `--type-sm`:

> Try one of these, or ask anything else:

**The four locked example questions** (parity-balanced across manufacturers and topics):

| Example | Topic | Manufacturer / coverage |
| --- | --- | --- |
| *"How do I fix the trough opto on Bond Premium?"* | `repair` | Stern, current — demonstrates in-coverage repair Q |
| *"What's the rule sheet for Wonka?"* | `gameplay` | JJP, current — demonstrates in-coverage gameplay Q |
| *"What's a Galactic Tank Force selling for these days?"* | `market` | AP, current — demonstrates the partial-answer + plural-marketplace-routing pattern |
| *"Who designed Twilight Zone?"* | `general` (credits) | Williams, defunct — demonstrates routing to IPDB/OPDB for pre-2010 machines, honest about coverage gap |

**Why exactly these four:**

- **Distribution across manufacturers** — Stern, JJP, AP, Williams. Cycles through; doesn't repeat any manufacturer. Williams included specifically to demonstrate honest coverage handling of pre-2010 machines.
- **Distribution across question topics** — repair, gameplay, market, general. Four of the six taxonomy values; doesn't try to cover all six (that would feel exhaustive in a way that fights cinematic-flourish).
- **Demonstrates the routing pattern, not just first-party answers** — the market and credits examples both involve routing to community resources. This is the Wizard showing what it actually does, not just what it knows.

**Visual:** each example question is a small recessed-puck CTA, same family as the routing-recommendation CTAs in the refusal panel — `--font-display` 500 `--type-sm`, `--bg-surface` background, `--border-quiet` border, hover lights `--border-glow-primary`. Click loads the question into the input and submits.

**Layout:** stacked vertically on mobile, 2-column grid on desktop. Equal visual weight (peer parity per appearance-of-favoritism rule — no example is "featured" over another).

**Refresh policy:** the four examples are LOCKED for v1. They don't rotate; they don't randomize; they don't personalize. Stable example set means returning users learn the Wizard's range over time without confusion. When the example set needs updating (new machines, new patterns), it updates as a deliberate decision logged in this doc's iteration log.

### Section 4 — Coverage summary + footer

A single-line coverage statement immediately above the standard footer:

**Copy:**

> The Wizard has first-party data on 8 active manufacturers and OPDB. Everything else routes to community resources. [What we cover →](what-we-cover.md)

**Visual:** `--font-body` `--type-sm` `--text-secondary`, centered. The "What we cover →" link is in `--accent-grounded` underline-on-hover.

Standard footer (project name, version, GitHub link, last-updated) renders below per the answer-screen pattern.

---

## Per-state variants

The empty/landing screen is mostly a single state, with two nuances:

### State — Initial cold load (default)

The composition above. Hero renders, ambient pulse begins, input auto-focuses.

### State — Returning visitor

For users with prior session activity (detectable via localStorage), one subtle adjustment:

- The suggested-question helpers section gains a small `--font-body` `--type-xs` `--text-secondary` line above it: *"Or ask something new."* — gentle acknowledgment that they've been here before, no familiarity-creep ("welcome back, Jim!" is the WRONG energy for this app).
- Otherwise unchanged. We don't show "your last question" or "your search history" — the no-engagement-metric posture forbids it.

If localStorage is empty or cleared: indistinguishable from initial cold load. Returning-user state is purely additive, never required.

---

## Mobile vs desktop

### Mobile (< 768px)

- Hero typography: `--type-2xl` at 48px. Wordmark may need to wrap if device is very narrow (< 360px) — the design tolerates wrap.
- Question input: stacked with submit button below (matches answer-screen mobile pattern).
- Suggested-question helpers: stacked single-column.
- **Auto-focus on mount disabled on mobile** to avoid pop-up keyboard hijacking the entry experience. Input is still tappable; user taps to begin.

### Desktop (≥ 768px)

- Hero typography: `--type-2xl` at 64px. Wordmark sits on a single line.
- Question input: inline with submit button to the right.
- Suggested-question helpers: 2-column grid.
- Auto-focus on mount enabled.

---

## Interaction details

### On page load

- Render all zones immediately (no progressive disclosure / no fade-in cascade — empty/landing is the calmest state, so it resolves quickly).
- Begin ambient pulse on the lit-insert element after `--motion-medium` (240ms) delay, so the pulse doesn't fight the page-render.
- Auto-focus the question input (desktop only — see Mobile above).

### Click on a suggested-question helper

- Load the example question text into the input (visible to the user — they can edit before submit).
- Auto-submit after 100ms (just enough time for the user to see the text appear, but not so long that it feels delayed).
- Page transitions to the answer screen's loading state.

### Click on "What we cover →"

- Navigates to the what-we-cover screen. NOT a modal, NOT inline expansion.

### Click submit / Enter on input

- Standard answer-screen submit behavior. Loading state begins.

---

## Accessibility

- Hero typography: `<h1>` element for the wordmark. The tagline is a `<p>` with `aria-hidden="true"` (decorative — the wordmark + subline carry the meaning). The subline is a `<p>` immediately following the `<h1>`.
- Ambient pulse element: pure decoration, `aria-hidden="true"`. Pulse animation is removed entirely under `prefers-reduced-motion: reduce`.
- Suggested-question helpers: each is a `<button>` with `aria-label="Try the example question: [question text]"`.
- Auto-focus on input: respects `aria-live` regions if any are present (none on this screen).
- Coverage summary link: standard `<a>` with descriptive text, no `aria-label` needed.

---

## Out of scope for this spec

- **The post-submit answer screen** — covered in `answer-with-citations.md`.
- **First-run onboarding modal / tour** — explicitly NOT in the design. Users learn the Wizard by using it. The suggested-question helpers are the implicit tour.
- **Sign-up / account creation** — not in v1 anywhere. The empty/landing screen does not gate access behind any form.
- **Themed hero variants** — when sibling themes ship (per `docs/ui/themes/sibling-themes-overview.md`), each may want its own hero treatment (e.g., DMD Classic might render the wordmark in pixel font). Defer to when at least one sibling earns a full spec.
- **Social sharing** — not on this screen. If sharing ever ships, it's per-answer (a "share this answer" affordance on the answer screen), not per-empty-state.

---

## Iteration log

| Date | Change | Rationale |
| --- | --- | --- |
| 2026-05-08 | v1 spec | Operationalizes the cinematic-flourish moment from the theme doc into the cold-load screen. Locks: hero composition (tagline + wordmark + subline + ambient pulse — no imagery, mechanics-not-IP), four parity-balanced suggested-question helpers (Stern repair / JJP gameplay / AP market / Williams credits — covers four manufacturers and four topics, demonstrates routing patterns alongside first-party answers), no-engagement-metric anti-patterns (no trending, no popular, no testimonials, no signup gate), returning-visitor state (subtle additive only, no familiarity-creep). Introduces one new visual token (`--type-2xl` = 48/64px, empty-screen-only). |
