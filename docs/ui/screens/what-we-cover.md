# What We Cover Disclosure Screen — Spec v1

> **Status:** v1 spec on `Dev-WebUiThemesBrainstorm`. Operationalizes the **coverage transparency** principle from `docs/community-resources.md` into a visible UI surface.

## Purpose

The contract's coverage-transparency principle says we must be openly honest about what data we have vs. what we don't. **This screen is where that honesty becomes visible to users.** Without it, coverage transparency is just text in a doc nobody outside the project will read.

This screen serves three audiences simultaneously:

1. **Sceptical users** evaluating whether the Wizard can answer their question before they bother asking.
2. **Sceptical prospects** evaluating the project as showcase material — looking for "do they know what they don't know?"
3. **Community members** evaluating the Wizard's relationship with the venues they care about — looking for "is this thing taking from us, or routing to us?"

This spec consumes:
- [`docs/ui/themes/modern-lcd.md`](../themes/modern-lcd.md) — theme system
- [`docs/community-resources.md`](../../community-resources.md) — the contract this surface makes visible
- [`docs/ui/screens/answer-with-citations.md`](answer-with-citations.md) — the locked visual tokens (color, type, spacing, motion) inherited verbatim

It does NOT cover: the answer screen itself, settings (theme picker etc.), the empty/landing screen, machine-detail screens.

---

## Inherited tokens

All visual tokens (`--bg-base`, `--accent-grounded`, `--font-display`, `--space-3`, etc.) are **inherited from `answer-with-citations.md`** — same palette, type, spacing, motion. This screen does not introduce new tokens.

---

## Information architecture

The page communicates seven things, in this order. Order matters — early sections set scope; later sections add context.

| Order | Section | Purpose |
| --- | --- | --- |
| 1 | **Hero** | Set expectations in two sentences: what the Wizard knows directly, what it routes you to. |
| 2 | **What we have first-party** | The 8 active manufacturers + OPDB. Each as a source card with refresh cadence and last-sync timestamp. The trust-builder. |
| 3 | **What we route you to** | The community-resource directory in user-facing categorical form (Catalogs, Marketplaces, Forums, News, Locations & Tournaments). Plurality posture explicit. |
| 4 | **What we don't cover** | Honest scope-limit statement. Each gap names where the user *can* go. |
| 5 | **How refusals work** | Three-paragraph explainer of the three refusal categories. Demystifies the "I don't know — but here's where to ask" pattern. |
| 6 | **Permissions we've asked for** | One-line status of the pricing-aggregator outreach. Updates as responses come in. |
| 7 | **Footer** | Project name, version, GitHub link, last-updated timestamp. |

---

## Screen zones (top-to-bottom)

Same five-zone composition as the answer screen, with the Answer Zone replaced by the disclosure content:

1. **Header zone** — same as answer screen. "What we cover" link in the header is highlighted on this screen (current-page state).
2. **Question input** — present but secondary. Disclosure pages still let users ask questions; the input serves as a "go ahead and ask" affordance.
3. **Disclosure content zone** — the main payload (sections 1–7 above). Replaces the answer zone.
4. **Citation card stack** — absent. (No active answer to cite on this screen.)
5. **Footer zone** — same as answer screen.

---

## Per-section spec

### Section 1 — Hero

A two-sentence statement, generous spacing.

**Copy:**

> # What the Wizard Knows
>
> The Wizard answers questions using two kinds of sources: **first-party data we scrape directly** from manufacturer game pages, service bulletins, and OPDB; and **community resources we route you to** — Pinside, IPDB, IFPA, marketplaces, forums, and more. This page tells you exactly what's in each bucket, when we last refreshed it, and what we don't cover.

**Visual:**
- `<h1>` styled as `--font-display` 700 `--type-xl` (32px on desktop, scales to 28px on mobile). Color `--text-primary`.
- Body text `--font-body` `--type-md` (18px) `--text-primary`. Two sentences max.
- Generous `--space-6` (48px) below the hero before Section 2 begins.
- No accent borders on the hero — calm entrance, not a callout.

### Section 2 — What we have first-party

Source cards, one per first-party source. Currently 9 cards: 8 manufacturers + OPDB.

**Section header:**

> ## What we have first-party

**Section sub-text** in `--text-secondary` `--type-sm`:

> We scrape these sources directly with explicit politeness — sitemap-driven, identifying user agent, respecting robots.txt unconditionally. Every cited source on the Wizard's answers links back to the canonical document on the source's own site.

**Source card anatomy** — each card carries six slots, in order:

| Slot | Treatment |
| --- | --- |
| **Source name** | `--font-display` 700 `--type-lg`, in `--text-primary` (e.g., "STERN PINBALL", "OPDB"). |
| **What we cover** | `--font-body` `--type-base`, e.g., "Game pages (75 games), service bulletins (86 bulletins), manuals." Counts driven from the catalog at render time. |
| **Refresh cadence** | `--font-body` `--type-sm` `--text-secondary`. e.g., "Daily polite scrape." |
| **Last sync** | `--font-mono` `--type-sm` `--text-secondary`. Timestamp e.g., "`2026-05-08 03:14 UTC`". The mono treatment signals "this is system-generated truth." |
| **Coverage notes** | `--font-body` `--type-sm` `--text-secondary` (optional — only when there's a meaningful caveat, e.g., "MSRP only — secondary-market value routes to plural marketplace set"). |
| **View source ↗** | Outbound CTA — same recessed-puck family as routing-recommendation CTAs but smaller (~32px tall). Backlit `accent-grounded`. Goes to the source's home page. |

**Card border:** `--border-quiet` 1px, `--radius-panel` (2px). Hover lights `--border-glow-grounded` (these ARE first-party authoritative sources).

**Layout:** stacked cards on mobile (full-width); 2-column grid on desktop. `--space-3` (24px) between cards.

**Per-source content (driven from a static config + live catalog data):**

| Source | What we cover (live counts in italics) | Refresh cadence | Coverage notes |
| --- | --- | --- | --- |
| Stern Pinball | Game pages, service bulletins, manuals | Daily | — |
| Jersey Jack Pinball | Game pages | Daily | — |
| American Pinball | Game pages | Daily | — |
| Spooky Pinball | Game pages | Daily | — |
| Pinball Brothers | Game pages | Daily | — |
| Barrels of Fun | Storefront product pages | Daily | — |
| Multimorphic | Game pages (P3 platform titles) | Daily | — |
| Chicago Gaming (CGC) | Williams/Bally remake game pages | Daily | — |
| OPDB | Canonical machine catalog | Daily sync | Modern + historic; cross-reference for everything |

### Section 3 — What we route you to

Categorical summary of the link-only destinations. NOT a complete directory dump (that's `community-resources.md`); a digestible user-facing version.

**Section header:**

> ## What we route you to

**Section sub-text** in `--text-secondary` `--type-sm`:

> When you ask about something we don't cover directly, we route you to the community resource best suited to your question. We treat these venues as peers — when multiple sources serve the same purpose (multiple marketplaces, multiple forums), we surface them all rather than picking a default. ([Read the full plurality posture](../../community-resources.md#destination-plurality--dont-pick-winners))

**Sub-sections** (each a categorical card with destinations listed alphabetically):

- **Catalogs** — IPDB, OPDB
- **Marketplaces** — Barnebys, eBay sold-listings, Liveauctioneers, Mr. Pinball Classifieds, PinballPrice, PinballPrices, PinballValue, Pinpedia, Pinside `/market`. Plus Craigslist, Discord, Facebook Marketplace named honestly as "venues we can't deep-link to but you should know about."
- **Forums** — Pinside Forum, Reddit /r/pinball, TiltForums
- **News** — manufacturer news pages, Pinball News, This Week in Pinball
- **Locations & Tournaments** — IFPA, Match Play Events, Pinball Map

**Visual:**
- Each category as a panel — `--bg-surface` background, `--border-quiet` border, `--radius-panel`.
- Category name as `--font-display` 700 `--type-md` `--text-primary`.
- Destination list as `--font-body` `--type-base`. Each destination is a quiet inline portal (subtle `↗` icon, `accent-grounded` underline on hover) per the contract's body-text portal pattern.
- Stacked vertically on mobile and desktop; reading-friendly width.
- `--space-3` between category panels.

### Section 4 — What we don't cover

Direct, honest, never apologetic. Each gap names where the user *can* go.

**Section header:**

> ## What we don't cover

**Copy:**

> The Wizard's first-party data is limited to the 8 active manufacturers and OPDB above. We don't have direct data for:
>
> - **Pre-2010 machines from defunct manufacturers** (Williams, Bally, Gottlieb, Data East, Sega, etc.) — see IPDB for academic-grade historical coverage and OPDB for cross-references.
> - **Current secondary-market pricing** — see the marketplace destinations above. We have new-machine MSRPs from the active manufacturers we scrape; everything beyond that routes out. ([Read the full v1 pricing strategy](../../community-resources.md#v1-pricing-strategy--first-party-msrp--aggregator-link-only))
> - **Tournament and competitive-play data** — see IFPA and Match Play.
> - **Where machines are physically located** — see Pinball Map.
> - **Detailed repair, modding, and gameplay-strategy discussion** — see Pinside Tech, Reddit /r/pinball, TiltForums.
> - **Designer / artist / code-author backgrounds beyond what's in OPDB and manufacturer pages.**

**Visual:**
- Section header: `--font-display` 700 `--type-lg`, `--text-primary`. NOT in `--accent-refusal` — coverage gaps are factual, not refusals.
- Body text: `--font-body` `--type-base`, `--text-primary`.
- Bulleted list with `--space-2` between items.
- Each "see [destination]" reference is a quiet inline portal.

### Section 5 — How refusals work

Demystifies the refusal pattern so users understand what to expect when the Wizard doesn't answer.

**Copy:**

> ## How refusals work
>
> When the Wizard isn't confident enough to answer, it doesn't make something up. It tells you it doesn't know, names why, and routes you to the community resources best suited to your question. There are three refusal categories:
>
> - **Low confidence** — the available sources don't directly address your question. We show you which community venues *do* answer this kind of question, in alphabetical order.
> - **Out of scope** — your question is about something we don't cover (per the section above). The category label changes; the routing recommendations stay relevant.
> - **Conflicting sources** — two cited sources disagree, and the Wizard refuses to pick. We show both citations and route you to community discussion to resolve.

**Visual:**
- Standard section treatment.
- The three refusal-category names rendered in `--font-display` `--type-md` (slight emphasis without using accent colors that would imply alarm).

### Section 6 — Permissions we've asked for

A one-line status of pricing-aggregator outreach. Updates as responses come in.

**Copy (current state):**

> ## Permissions we've asked for
>
> On 2026-05-08, we reached out to these pricing aggregators to ask permission to surface their data inside the Wizard with full attribution: **PinballPrice, PinballPrices, Pinpedia, PinballValue**. Whether they say yes, no, or don't respond, we continue to route users to them — but a "yes" would let us show prices directly with attribution rather than just routing to their sites. ([Read the full outreach context](../../community-resources.md#v1-pricing-strategy--first-party-msrp--aggregator-link-only))

**Future copy patterns:**

- If an operator says yes: append a line under the relevant aggregator with their grant terms.
- If an operator says no: append a line acknowledging respectfully and noting we continue to route to them.
- If no response after some interval: no copy change required.

**Visual:**
- Standard section treatment.
- Operator names in `--font-display` 600 `--text-primary`.

### Section 7 — Footer

Project name, version (or last-updated date), GitHub link, and a one-liner about the project.

**Copy:**

> PinballWizard is a customer-facing showcase / reference application by Earlybird Solutions. Open source on [GitHub](https://github.com/Early-Bird-Solutions-LLC/PinballWizard).
>
> *This page last reflects the catalog and routing state as of `[live timestamp]`.*

**Visual:**
- `--font-body` `--type-sm` `--text-secondary`.
- Centered.
- `--space-5` above (visual separation from Section 6).

---

## Per-state variants

This is a mostly static screen, but two state nuances matter:

### State — Loading (initial page render)

If source-card data (counts, last-sync timestamps) isn't yet hydrated:
- Source cards render with placeholder dashes for counts and "checking…" for last-sync.
- Pulse on `--border-glow-grounded` until hydrated, same animation as the answer-screen loading.
- Once hydrated: pulse stops, real values appear with a brief glow flare.

### State — Stale-source warning

If any source's last-sync timestamp is older than its expected refresh interval × 1.5 (e.g., daily refresh that hasn't run in >36 hours):
- That source's card border tints `--accent-refusal` at low intensity (not full glow — quiet warning).
- A small `STALE` pill in `--accent-refusal` appears next to the last-sync timestamp.
- Card otherwise behaves normally — the user can still click through.

This is honesty about freshness — covered in coverage transparency. Users who care about recency can see at a glance if anything's behind.

---

## Mobile vs desktop

Same 768px breakpoint as the answer screen.

### Mobile (< 768px)

- Source cards: stacked single-column, full-width.
- Category panels (Section 3): stacked, full-width.
- Hero text: scales to `--type-lg` (24px) instead of `--type-xl`.
- Otherwise unchanged.

### Desktop (≥ 768px)

- Source cards: 2-column grid (4–5 rows of 2 cards each for the 9 sources).
- Category panels: stacked but with constrained max-width (~720px centered) for readability.
- Hero text: full `--type-xl`.

---

## Interaction details

### Navigation in / out

- **Entry:** "What we cover" link in the persistent header zone of every screen.
- **Exit:** Question-input remains active — submitting a question takes the user to the answer screen.
- **From a refusal:** every refusal panel includes a quiet "see what we cover" link in `--text-secondary` near the routing recommendations. Refusals are the moment this screen earns its keep.

### Click behaviors

- Source-card "View source ↗" CTA: opens the source's home page in a new tab.
- Inline destination portals (Section 3): open destination URLs in new tabs.
- Cross-references to `community-resources.md` sections: open the rendered version on GitHub in a new tab (NOT inline — the contract is a separate document, not part of the user-facing app).

### Keyboard navigation

- Tab order: header brand → "What we cover" link (currently focused-page state) → question input → submit button → source cards (each card focusable, Tab into → "View source ↗" Tab again to next card) → category panel inline portals (in source order) → "see what we cover" links in body → footer GitHub link.

---

## Accessibility

- All inherited tokens carry the AA-verified contrast values from the answer screen.
- "Stale" warning pill: paired with text label, never relies on color alone.
- Source-card timestamps: rendered in mono for readability and predictable screen-reader announcement (mono prevents the SR from misreading "0314" as a number).
- Cross-reference links to docs on GitHub: announced with `aria-label="Read the full [section name] in the contract document, opens in new tab"`.

---

## Out of scope for this spec

- **Settings screen** (theme picker, motion preferences, audio when v2). Covered in a separate spec.
- **Per-machine deep-detail.** A user wanting "what does the Wizard know about Godzilla Premium specifically?" goes to the machine-detail screen, not here. Here is the *meta* view of sources; there is the per-machine view of content.
- **Real-time outreach status** (operator responses arriving live). The Section 6 copy is updated manually as responses come in — outreach state lives in `memory/project_pricing_outreach_2026_05_08.md`, with this page reflecting the current snapshot. A future automation could sync the two but that's not v1.
- **Per-source diagnostics** (last-error timestamps, success/fail counts, scrape-throughput metrics). Operator-facing, not user-facing — belongs in an internal observability surface, not this disclosure page.

---

## Iteration log

| Date | Change | Rationale |
| --- | --- | --- |
| 2026-05-08 | v1 spec | Operationalizes the coverage-transparency principle from `community-resources.md` into a visible UI surface. Seven content sections (hero, first-party, route-to, what-we-don't-cover, how-refusals-work, permissions-asked, footer); source-card anatomy with live counts and freshness timestamps; stale-source warning state; navigation pattern from refusal panels; inherits all visual tokens from `answer-with-citations.md`. The screen makes coverage transparency real — without it the principle is just a doc. |
