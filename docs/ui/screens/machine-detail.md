# Machine Detail Screen — Spec v1

> **Status:** v1 spec on `Dev-WebUiThemesBrainstorm`. The per-machine deep-view surface — everything we know about one specific machine, organized by content type.

## Purpose

The answer-with-citations screen handles **questions about machines.** This screen handles **the machine itself.** Different mode, different IA: the user is here to *browse and verify* what's known about a specific machine, not to ask a question.

Reached from:
- Clicking an inline machine reference in an answer body (per the body-text portal pattern)
- Clicking a citation card's machine context (when the citation is about a specific machine)
- Direct URL navigation (`/machines/{slug}`) — bookmarkable, shareable
- Future: search results

Three audiences:

1. **Users with a specific machine in mind** — own one, considering buying one, fixing one — who want everything in one place rather than asking question-by-question.
2. **Prospects** — this is the "drill-down" surface that demonstrates the catalog's depth. The provenance tab specifically demonstrates the "provenance is sacred" locked invariant in user-visible form.
3. **Skeptics** — the provenance tab again. Anyone who wants to see "where exactly did this come from?" can verify every claim back to a source URL.

This spec consumes:
- [`docs/ui/themes/modern-lcd.md`](../themes/modern-lcd.md) — theme system
- [`docs/ui/screens/answer-with-citations.md`](answer-with-citations.md) — locked visual tokens (color, type, spacing, motion) inherited verbatim
- [`docs/community-resources.md`](../../community-resources.md) — routing contract this screen renders against, especially for the Community tab
- [`docs/ui/screens/what-we-cover.md`](what-we-cover.md) — the source-card anatomy reused for tab-content cards

It does NOT cover: the answer-with-citations screen, what-we-cover screen, settings, error states.

---

## Inherited tokens

All visual tokens from `answer-with-citations.md` and `empty-landing.md` carry through unchanged. **No new tokens introduced.** Tab styling reuses existing border / glow / spacing tokens.

---

## Information architecture

Top-down on the page:

| Order | Section | Purpose |
| --- | --- | --- |
| 1 | **Header** | Persistent header (brand mark + "What we cover" link) |
| 2 | **Question input** | Persistent input (per other screens — users can ask a question from any screen) |
| 3 | **Machine hero** | Identity zone — machine name, manufacturer, year, photo carousel, primary "visit official page" CTA, coverage-summary line |
| 4 | **Tab bar** | Five tabs: Manual / Bulletins / Specs / Community / Provenance |
| 5 | **Tab content** | The active tab's payload |
| 6 | **Footer** | Standard project footer |

The tab content area replaces what would be the answer zone on the answer screen. Citation card stack does not appear (cards live inside relevant tabs instead).

---

## Screen zones

### Header zone

Same as other screens. The "What we cover" link in the header is *not* highlighted on this screen (it's not the current page).

### Question input

Persistent. Submitting a question takes the user to the answer-screen flow — the machine context can be appended to the question if the user submits with empty machine-context-replacement disabled (out of scope for v1; submission goes straight to answer screen as authored).

### Machine hero

Identity zone. Composition:

| Slot | Treatment |
| --- | --- |
| **Machine name** | `--font-display` 700 `--type-xl` (32px) `--text-primary`. e.g., "GODZILLA (PREMIUM)". |
| **Manufacturer + year + edition** | `--font-display` 500 `--type-md` `--text-secondary`. e.g., "Stern Pinball  ·  2021  ·  Premium". |
| **Photo carousel** | If OPDB or manufacturer scraping has photos for this machine: an aspect-ratio-preserved image carousel, `--bg-surface` background, `--border-quiet` border, `--radius-panel`. If no photos: skip the carousel entirely (no placeholder image — empty space is more honest than a stock photo). |
| **Coverage-summary line** | `--font-body` `--type-sm` `--text-secondary`. e.g., "First-party: 2 manuals, 5 bulletins, full specs. Last refreshed 2026-05-08 03:14 UTC." Numbers driven from the catalog at render time. |
| **Primary outbound CTA** | The era-appropriate primary-canonical destination per the contract's machine-resolution table. For active machines: "Visit Stern's official Bond Premium page ▶" (recessed-puck family, `accent-grounded` backlight). For historic machines (pre-2010): "Visit IPDB entry ▶". |

**Spacing:** `--space-4` between hero slots. `--space-5` between hero and the tab bar.

### Tab bar

Five tabs, in this order: **Manual** · **Bulletins** · **Specs** · **Community** · **Provenance**.

| Property | Spec |
| --- | --- |
| Layout | Horizontal row, left-aligned. Each tab is a clickable button. |
| Tab label | `--font-display` 600 `--type-sm` ALL CAPS. |
| Active tab | `--text-primary` color + `--border-glow-grounded` 2px bottom border. |
| Inactive tab | `--text-secondary` color, no bottom border. Hover: `--text-primary` + low-opacity `--border-glow-grounded` underline. |
| Empty/sparse-data tab | Same as inactive tab BUT a small subscript count appears: e.g., "BULLETINS · 0". The user sees "this tab exists, but we have nothing for this machine." |
| Mobile | Horizontal scroll if tab bar exceeds viewport width. No collapse to dropdown. |
| Spacing | `--space-3` between tabs. `--space-1` padding above/below tab labels. `--border-quiet` 1px line below the tab bar separating it from tab content. |

### Tab content area

Renders the active tab's payload. `--space-4` of spacing between tab bar and content.

### Footer

Standard project footer per other screens.

---

## Per-tab spec

### Tab 1 — Manual

Lists all manual documents for this machine.

**Populated state:**
- Each manual rendered as a **citation card** per the anatomy locked in [`modern-lcd.md`](../themes/modern-lcd.md#citation-card-anatomy). Source-type tag is `MANUAL`. Excerpt is the manual's title or first heading if scraped; otherwise a short description.
- Cards stacked vertically, `--space-3` between.
- Each card's right flipper opens the canonical manual URL; left flipper is hidden on this screen (no "view in answer" context here — that's only meaningful when the user reached this card from an answer).

**Empty state:** if no manuals are scraped for this machine:
- Message: *"We don't have manuals scraped for [machine]."*
- Routing recommendation (single CTA): *"Try [manufacturer]'s official manual page ▶"* if the manufacturer has a manual page, OR *"Try IPDB's manual archive for this machine ▶"* if historic.
- No "no data" placeholder image. Honest empty space + a route-out is the answer.

### Tab 2 — Bulletins

Service bulletins for this machine.

**Coverage note** at top of tab in `--text-secondary` `--type-sm`:
> Service bulletins are currently only scraped for Stern machines. For other manufacturers, we route you to their official support pages.

**Populated state (Stern machines):**
- Bulletins rendered as citation cards per the same pattern. Source-type tag is `BULLETIN`. Excerpt is the bulletin's title or summary.
- Sorted by bulletin date descending (newest first).

**Empty state for non-Stern machines:**
- Message: *"Service bulletins for non-Stern machines aren't currently scraped."*
- Routing recommendation: *"Try [manufacturer]'s official support page ▶"* (if active) plus *"Pinside Tech forum machine-specific subforum ▶"* (per the routing matrix's `repair` row).

**Empty state for Stern machines with zero bulletins:**
- Message: *"No service bulletins scraped for [machine] yet."*
- Same Pinside Tech forum routing as fallback.

### Tab 3 — Specs

Structured data table + pricing context + peer canonical-source links.

#### Specs table

Rendered as a `<dl>` (definition list) for semantic structure. Two columns: field name (`--font-body` `--type-sm` `--text-secondary` ALL CAPS) and value (`--font-body` `--type-base` `--text-primary`).

| Field | Source |
| --- | --- |
| Title | Catalog (manufacturer scrape) |
| Manufacturer | Catalog |
| Year | Catalog |
| Edition (Pro / Premium / LE / etc.) | Catalog |
| Theme | Catalog (where available) |
| Designer(s) | Catalog or OPDB cross-reference |
| Code / firmware version | Catalog (active machines only) |
| MSRP at release | Catalog (active machines only) |
| OPDB ID | OPDB sync |
| Production status (current / discontinued) | Manufacturer scrape |

Fields with no data are simply omitted (not rendered as "—" or "unknown"). Honest gaps without false structure.

Each value carries a small attribution hover-tooltip: hovering reveals "Source: [where this value was scraped from]" with a click-through to the specific source URL. Tooltip styling: same as inline citation marker hover-tooltip.

#### Pricing context sub-section

Below the specs table. `--font-display` 600 `--type-md` `--text-primary` heading: "PRICING CONTEXT".

If MSRP exists: a one-liner statement of MSRP at release with attribution.

Then the standard plural marketplace routing — same set as the refusal-routing matrix's `market` row, alphabetical, peer-parity treatment:
- Barnebys ▶
- eBay sold-listings search ▶
- Liveauctioneers ▶
- Mr. Pinball Classifieds ▶
- PinballPrice ▶
- PinballPrices ▶
- PinballValue ▶
- Pinpedia ▶
- Pinside `/market` ▶

Plus the honest "venues we can't deep-link to" mention: *"Facebook Marketplace and regional Facebook pinball groups are also where many private sales happen — no direct link possible."*

#### Peer canonical-source links sub-section

Below pricing. `--font-display` 600 `--type-md` heading: "OTHER REFERENCES".

Per the contract's plurality principle, machine references surface OPDB *and* IPDB as peer canonicals. Render BOTH as outbound CTAs. If a Pinside game page is constructible from the slug (per the resolver), that's a third entry.

For active machines: also surface the manufacturer's official game page if not already represented in the hero CTA.

### Tab 4 — Community

Routing recommendations to community resources for THIS specific machine. Per the contract's plurality principle, surface plural sets per category, alphabetical within each set.

#### Section: Forum / discussion

Routing recommendations:
- Pinside game-page forum ▶ (`pinside.com/pinball/machine/{slug}/forum`)
- Reddit /r/pinball discussion ▶ (constructed search URL for the machine)
- TiltForums ▶ (machine-specific subforum if exists, else general forum)

#### Section: Marketplace

Already covered in the Specs tab's pricing-context sub-section. This tab's Marketplace section is a deliberate cross-reference, not a duplicate render: a single line of text — *"Marketplace destinations are listed in the Specs tab's pricing-context section."* — with a link to the Specs tab.

(Why not duplicate? Because rendering 9 marketplace CTAs in two tabs creates visual clutter and reads as inflation. One canonical render in Specs.)

#### Section: Where to play

Routing recommendations:
- Pinball Map machine search ▶ (per the contract's `location` row)

#### Section: Tournaments

If the machine appears in tournament contexts (e.g., a competitive-play favorite): routing recommendations to:
- IFPA event search ▶
- Match Play public events ▶
- Pinside per-event pages ▶ (if any reference this machine)

For machines that don't appear in tournament contexts: this section is omitted (not rendered as empty). Tournament data is sparse for v1.

#### Visual treatment

All routing CTAs use the recessed-puck family (same as refusal-panel routing recommendations). All `--accent-grounded` backlit. Peer parity: equal visual weight, alphabetical ordering within each section. Sections separated by `--space-4`.

### Tab 5 — Provenance

The locked-invariant-1 "provenance is sacred" trust surface. Lists every piece of data we have about this machine and exactly where it came from.

#### Layout

Tabular layout (rendered as `<table>` for semantic clarity). Columns:

| Field | Value | Source URL | First discovered | Last verified |
| --- | --- | --- | --- | --- |
| Title | Godzilla (Premium) | sternpinball.com/game/godzilla-premium | 2026-04-12 | 2026-05-08 |
| Manufacturer | Stern Pinball | sternpinball.com/game/godzilla-premium | 2026-04-12 | 2026-05-08 |
| Year | 2021 | sternpinball.com/game/godzilla-premium | 2026-04-12 | 2026-05-08 |
| OPDB ID | GBLld-MQK0X | opdb.org/api/machines/GBLld-MQK0X | 2026-04-12 | 2026-05-08 |
| Bulletin SB-243 | (full title) | sternpinball.com/support/service-bulletins/sb-243 | 2026-04-12 | 2026-05-08 |
| Manual: Operations Manual | (full title) | sternpinball.com/manuals/godzilla-pe-operations-manual.pdf | 2026-04-12 | 2026-05-08 |
| ... | ... | ... | ... | ... |

(Driven from the catalog's per-item provenance metadata at render time.)

#### Visual

- Mono treatment for source URLs and dates (`--font-mono` `--type-sm`).
- Field names in `--font-body` 500 `--type-sm` `--text-secondary`.
- Values in `--font-body` `--type-base` `--text-primary`.
- Source URLs are clickable — opens canonical URL in new tab.
- Sortable column headers (sort by field, value, first-discovered, last-verified).
- On mobile: table collapses to stacked cards, one per data row, with field labels above each value.

#### Coverage trail

Below the table, a small note in `--text-secondary` `--type-sm`:

> Every value above traces back to a specific source URL we scraped. The catalog also tracks ETag and Last-Modified headers per source; if a source changes, we capture the new version with a fresh "first discovered" date. This is the provenance chain that grounds every Wizard answer.

This explainer demystifies what the data trail actually represents — important for the prospect audience.

---

## Per-state variants

### State — Loading

Standard loading pattern: hero zone renders with placeholder dashes for counts and "checking…" for last-refreshed. Tab bar renders without sub-counts. Tab content area shows pulse on `--border-glow-primary`. Once hydrated, all values appear with brief glow flare.

### State — Machine not found

If the slug doesn't match any catalog entry:
- Hero replaced with: `MACHINE NOT FOUND` callout in `--accent-refusal` (display, ALL CAPS).
- Reason text: *"We don't have first-party data on a machine matching `{slug}`. The Wizard's first-party data is limited to active manufacturers — see What we cover."*
- Routing recommendations (per `LOW_CONFIDENCE × general`):
  - IPDB search for "{slug}" ▶
  - OPDB search for "{slug}" ▶
  - Pinside search ▶ (if a Pinside slug can be constructed)
- Honest empty: no hallucinated content, no "did you mean…" suggestions (those drift toward engagement-metric framing).

### State — Tab with zero content

Per per-tab specs above. Each empty state names the gap honestly + routes out.

### State — Stale data

If the machine's last-refreshed timestamp exceeds expected refresh interval × 1.5:
- Coverage-summary line in hero gets a `STALE` pill (matches the what-we-cover screen pattern).
- Each tab's data still renders normally — staleness is a freshness signal, not a content block.

---

## Interaction details

### URL structure

- Default: `/machines/{slug}` — opens to the Specs tab (the most universally-populated tab).
- Tab-deep-link: `/machines/{slug}/{tab}` where `tab ∈ {manual, bulletins, specs, community, provenance}`.
- Browser back/forward navigates between tab states.
- All tab URLs are bookmarkable and shareable.

### Tab switching

- Click a tab: URL updates, tab content fades in over `--motion-fast` (120ms). No fade-out of previous content (instant replacement) — the cinematic-flourish cross-fade pattern is reserved for mode-start moments, not tab switches.
- Active tab visual moves immediately; doesn't animate.

### Click on a citation card flipper button

- Right flipper (VIEW THE ORIGINAL ▶): opens canonical URL in new tab. Standard.
- Left flipper (VIEW IN ANSWER ◀): hidden on machine-detail screen by default — there's no answer context here. If the user arrived from an answer screen, the left flipper is shown and returns them to that answer scrolled to the relevant inline marker (browser history).

### Click on hero photo carousel

- Tap/click advances to the next photo. Keyboard arrows also work. Swipe on mobile.
- Click on an individual photo opens it at full size in a lightbox-style overlay. Lightbox is the only modal pattern this app uses; otherwise we route out.

### Click on attribution tooltip in Specs

- Tap reveals tooltip; tap-elsewhere dismisses. Click on the source-URL link in the tooltip opens it in new tab.

### Click on Provenance source URL

- Opens canonical URL in new tab.

### Question input on this screen

- Submitting a question takes the user to the answer-screen flow. The machine context (current machine slug) is NOT auto-injected into the question — the user types what they want to ask. (Auto-injection feels like presumptuous magic; explicit asking is more honest.)

---

## Mobile vs desktop

### Mobile (< 768px)

- Hero photo carousel: smaller (16:9 aspect ratio capped at 240px tall).
- Tab bar: horizontal scroll. NO collapse to dropdown — the tabs are always visible to scroll through.
- Specs table: rendered as definition list (already structured that way; mobile-friendly by default).
- Provenance table: collapsed to stacked cards, one per data row.
- Routing CTAs in Community tab: stacked vertically, full-width.

### Desktop (≥ 768px)

- Hero photo carousel: 16:9 aspect ratio, capped at 480px tall, centered.
- Tab bar: horizontal row, no scroll needed for 5 tabs.
- Specs table: 2-column definition list, max content width.
- Provenance table: full table layout.
- Routing CTAs in Community tab: side-by-side per section if 2-3 fit, otherwise stacked.

---

## Accessibility

- Tabs implemented as ARIA-tabs pattern: `role="tablist"`, each tab `role="tab"`, panels `role="tabpanel"` with proper `aria-controls` / `aria-labelledby` linkages.
- Keyboard: Left/Right arrows navigate between tabs; Enter activates; Home/End jump to first/last tab.
- Photo carousel: prev/next buttons are real `<button>` elements with `aria-label`. Each image has `alt` text from the catalog (manufacturer-provided where possible; "Photo of {machine}" fallback).
- Provenance table: proper `<th scope>` markup; sortable headers announce sort state.
- Lightbox: `role="dialog"` with focus trap; Escape closes.

---

## Out of scope for this spec

- **Machine search / browse** (the way users find machines without arriving via an answer's inline reference). Future spec — likely lives at `/machines` index page.
- **Inter-machine comparison** (e.g., "compare Pro vs. Premium"). Defer; comparison is a different mode.
- **Owner-submitted content** (reviews, photos, mods). NOT a thing for this app — community-resource posture says we route to Pinside / etc. for that, never host it ourselves.
- **Print/export view** of the machine page. Not v1.
- **Embed view** for sharing on other sites. Not v1.

---

## Iteration log

| Date | Change | Rationale |
| --- | --- | --- |
| 2026-05-08 | v1 spec | Operationalizes the per-machine deep-view surface. Five tabs locked: Manual / Bulletins / Specs / Community / Provenance. Hero zone with photo carousel (no stock-photo fallback — honest empty space), coverage-summary line, era-appropriate primary CTA. Provenance tab specifically demonstrates the "provenance is sacred" locked invariant in user-visible form — every value traces to source URL. Honest empty states throughout (machine-not-found, sparse-tab, stale-data). URL structure supports tab-deep-linking. Inherits all visual tokens — no new ones introduced. |
