# Brainstorm Handoff — `Dev-WebUiThemesBrainstorm`

> **Status:** Active design brainstorm. Branch produces design artifacts (specs, prototypes, contract additions, outreach tooling) — no production code. **Ready to graduate to implementation when desired**: the artifacts are coherent enough to be the seed of an actual implementation PR.
>
> **Started:** 2026-05-08. **Worktree:** `.claude/worktrees/peaceful-lewin-685cd6/`
>
> **Single-paragraph framing:** This branch designs PinballWizard's user-facing surface (web UI themes, screen specs, community-resource routing contract, operator-outreach tooling) before any frontend implementation begins. The thesis: get the *posture* right (community-resource, plurality, appearance-of-favoritism, citation-as-hero, polite-by-construction at the UI layer) before writing Blazor code, because the posture shapes every visual and structural decision. Once the design is right, the implementation is translation, not invention.

## What's been built

### Specs (1 theme system, 1 sibling overview, 4 screens)

| Spec | Path | State |
|---|---|---|
| Modern LCD theme system | [`docs/ui/themes/modern-lcd.md`](ui/themes/modern-lcd.md) | **Locked v3.3** — palette, type, panel grammar, motion vocabulary, surface inventory, citation-as-hero, refusal panel, four Posture rules with anti-patterns |
| Sibling themes overview | [`docs/ui/themes/sibling-themes-overview.md`](ui/themes/sibling-themes-overview.md) | **Sketched** — DMD Classic, Cabinet, Backbox, Score Reel, Daytime Route + the derivation principle ("siblings are skins") |
| Answer-with-citations screen | [`docs/ui/screens/answer-with-citations.md`](ui/screens/answer-with-citations.md) | **Locked v1** — concrete visual tokens, 6 state variants, mobile/desktop, full a11y |
| What-we-cover disclosure | [`docs/ui/screens/what-we-cover.md`](ui/screens/what-we-cover.md) | **Locked v1** — coverage-transparency surface, source cards, stale-source warning |
| Empty / landing | [`docs/ui/screens/empty-landing.md`](ui/screens/empty-landing.md) | **Locked v1** — cinematic-flourish hero, suggested-question helpers, no-engagement-metric anti-patterns |
| Machine detail | [`docs/ui/screens/machine-detail.md`](ui/screens/machine-detail.md) | **Locked v1** — 5-tab layout (Manual / Bulletins / Specs / Community / Provenance), per-state variants |

### Prototypes (working HTML / CSS / JS, openable in any browser)

| Prototype | Path | What it proves |
|---|---|---|
| Answer screen — Modern LCD | [`docs/ui/prototypes/answer-with-citations.html`](ui/prototypes/answer-with-citations.html) | The default theme rendered with full citation cards, flipper buttons, inline pinball-insert markers. Implementation-ready visual. |
| Answer screen — DMD Classic | [`docs/ui/prototypes/answer-with-citations-dmd-classic.html`](ui/prototypes/answer-with-citations-dmd-classic.html) | Sibling-theme derivation works in the dark-axis: same scaffold, swap palette + pixel font, get a coherent retro variant. |
| Answer screen — Daytime Route | [`docs/ui/prototypes/answer-with-citations-daytime-route.html`](ui/prototypes/answer-with-citations-daytime-route.html) | Dark→light palette inversion works: most extreme token swap of any sibling. |
| Answer screen — Backbox | [`docs/ui/prototypes/answer-with-citations-backbox.html`](ui/prototypes/answer-with-citations-backbox.html) | Most visually maximalist sibling: deep blue-black + magenta/cyan/violet with heavy outer glow. Big Shoulders Display for cinematic display type. Proves the design system handles the "more is more" end of the spectrum without crossing into marketing-site territory. |
| Refusal state | [`docs/ui/prototypes/refusal-state.html`](ui/prototypes/refusal-state.html) | `LOW_CONFIDENCE × market` refusal with 9-CTA marketplace plural set, peer-parity equal-weight. |
| Empty / landing | [`docs/ui/prototypes/empty-landing.html`](ui/prototypes/empty-landing.html) | Cinematic-flourish hero with no machine imagery (mechanics-not-IP), ambient amber pulse, 4 parity-balanced suggested-question helpers. |
| Theme picker | [`docs/ui/prototypes/theme-picker.html`](ui/prototypes/theme-picker.html) | **Live theme switching** across all 3 prototyped themes via CSS-variable swap + localStorage persistence. The architectural proof. |

### Community-resources contract

[`docs/community-resources.md`](community-resources.md) — **v1.7**. The system contract that AI agents, refusal logic, the resolver, and the UI all build against. Locks:

- **Posture** — community resource (route outward, not capture); the umbrella **Avoiding the appearance of favoritism** principle with **Destination plurality** and **Coverage transparency** as flowing sub-principles
- **Destination directory** — 8 active manufacturers + 13 community catalogs / tools / marketplaces (alphabetically grouped), plus 3 "venues we can't deep-link to" (Facebook Marketplace, Discord, Craigslist) for honest naming in refusal text
- **Question topic taxonomy** — closed 6-value enum (`repair`, `gameplay`, `market`, `location`, `tournament`, `general`)
- **Refusal-routing matrix** — per `(category × topic)`, alphabetical plural sets
- **Resolver implementation sketch** + the Pinside slug-resolution problem (newer titles prefix manufacturer; needs hand-curated alias table)
- **v1 pricing strategy** — first-party MSRPs + aggregator-link-only

### Outreach skill + sent emails

- **Skill:** `~/.claude/skills/earlybirdsolutions-outreach/` — global personal skill. SKILL.md + `templates/community-operator-outreach.md` + `signature.md`. Voice / posture guidance, per-recipient checklist, Gmail-MCP draft creation workflow (drafts only, never auto-send).
- **4 outreach emails sent 2026-05-08** from `jim@earlybirdsolutions.com` to PinballPrice, PinballPrices (Doc Finlay), Pinpedia, PinballValue. Each asks for API access OR once-daily polite scraping with attribution + purpose-bound use ("router signal only — Wizard never facilitates transactions"). Compliance verified before sending: none have ToS prohibiting scraping; 3 of 4 robots.txt explicitly allow; the 4th has no robots.txt; Pinpedia privacy policy verified silent on automated access.
- **Project memory:** [`memory/project_pricing_outreach_2026_05_08.md`](../../../Users/JimKeeley/.claude/projects/C--projects-PinballWizard/memory/project_pricing_outreach_2026_05_08.md) — full record + how-to-handle-responses (yes / no / counter-offer / no-response).

### Memory entries (5 created or updated this branch)

| Entry | Type | Captures |
|---|---|---|
| `feedback_community_resource_posture.md` | global feedback (new) | Route outward, never capture; outbound is a feature; refusal directs out; no engagement-metric framing |
| `feedback_destination_plurality.md` | global feedback (new) | Surface multiple venues as plural sets; never pick a default; alphabetical or randomized ordering |
| `feedback_avoid_appearance_of_favoritism.md` | global feedback (new) | Umbrella principle covering ordering, visual parity, coverage transparency, manufacturer / brand parity, refusal framing |
| `project_pricing_outreach_2026_05_08.md` | project (new) | Pricing-aggregator outreach in flight + response-handling guidance |
| `feedback_personal_identity_only.md` | global feedback (updated) | `jim@earlybirdsolutions.com` reframed as project-public (2026-05-09); `PERSONAL_IDENTITY_PATTERN` secret deleted |

(`MEMORY.md` index updated to surface all of the above for future sessions.)

### Shipped to `main` during this branch

- **PR #133** — `docs: add plain-language community-data-attribution one-pager (for outreach replies)` — landed `docs/community-data-attribution.md` so the URL referenced (implicitly) by the outreach emails is live. Required deleting the `PERSONAL_IDENTITY_PATTERN` repo secret to unblock the sanitization scan. Awaiting your merge timing. URL: <https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/133>

## Locked decisions

These are settled. Re-opening any requires explicit reason, not preference.

### Architecture
- **Themes are CSS-variable token sets.** HTML scaffold is theme-agnostic. "Siblings are skins, not different apps." Visually proven by 3 working themes via the theme-picker prototype.
- **Default theme:** Modern LCD. Most contemporary, most prospect-friendly, most accessible.
- **No machine imagery, no manufacturer logos, no game art** anywhere in the visual system. Theme names evoke pinball *mechanics* (DMD, Cabinet, Backbox, Score Reel) — never specific games or brands.

### Visual system
- **Display font for Modern LCD:** Barlow Condensed 700 primary / 500 secondary. Spike resolved against Saira (drift to gaming-HUD), Oswald (overuse-disqualified), Anton (fails small-caps).
- **Body font:** Inter (universal across themes).
- **Mono font:** JetBrains Mono (Modern LCD + Daytime Route); Press Start 2P at small size for DMD Classic.
- **WCAG AA conformance** verified for the Modern LCD palette per-pair (computed contrast values). Required for any sibling.
- **`prefers-reduced-motion: reduce`** strips all animation to 0ms. The flipper-press depression is the one earned tactile motion exception.

### Interaction patterns
- **Citation as hero.** Every answer's sources are full-fidelity cards with flipper-button CTAs (`◀ VIEW IN ANSWER` left, `VIEW THE ORIGINAL ▶` right). Never collapsed behind a disclosure.
- **Inline citation markers:** numbered pinball-insert style (small accent-grounded glowing circles). Not academic superscript. Not named pills.
- **Body-text outbound portals:** machine names, manufacturers, tournaments in body text are quiet inline portals (subtle ↗ icon, accent-grounded underline on hover). Single-primary destination per inline reference (paragraph rhythm).
- **Refusal directs out.** Every refusal panel routes to a community resource that *can* answer. Honest naming of coverage gaps in reason text.

### Posture
- **Destination plurality.** When multiple community venues serve the same purpose, surface them as a plural set (alphabetical or randomized). No editorial ordering. Visual parity across peers — no "primary" CTA elevated above its siblings.
- **Coverage transparency.** "What we cover" disclosure surface in v1 UI. Refusal panels honestly name gaps.
- **No engagement-metric framing.** No trending questions, no popular searches, no testimonials, no signup gate, no first-run tour, no session-history. Walling content behind disclosures or expanders is forbidden.

### Operational
- **v1 pricing strategy:** first-party MSRPs (already scraped) + aggregator-link-only for secondary market. Outreach-independent — works regardless of how operator responses land.
- **`jim@earlybirdsolutions.com` is project-public.** Reframed 2026-05-09. `PERSONAL_IDENTITY_PATTERN` secret deleted; `WORK_EMAIL_PATTERN` secret remains (set it if work email risks appearing in a file).

## Open questions (still in flight)

### Theme system
- **Sibling theme prototypes for Backbox, Cabinet, Score Reel** — sketched in `sibling-themes-overview.md` but not yet rendered.
- **Display font for Daytime Route** — Barlow (open) is the working pick; could spike against DM Sans Bold.
- **Theme-picker placement:** prototyped at top-of-page; locked to "lives on Settings screen" for production. Settings screen spec doesn't exist yet.

### Contract
- **Question-topic dominance heuristic** for multi-topic questions (single-topic-per-question is locked; need an explicit rule for picking the dominant one).
- **Validation batch infrastructure** for link health (Pinside specifically blocks programmatic UAs — needs alternative validation strategy).
- **ADR promotion** — community-resource posture is probably ADR-0025-worthy. Doc has stabilized; ADR draft is the next step. Requires explicit user confirmation before commit.

### Outreach
- **Operator responses** to the 4 sent emails. Response-handling guidance in `memory/project_pricing_outreach_2026_05_08.md`.
- A "yes" from any operator promotes their data from link-only to first-party-with-attribution; a "no" or no-response keeps things as they are.

### Visual / rendering
- **What reads off in the prototypes** — token values are committed but no real-eyes critique pass yet. Iteration may follow once you've spent time in the rendered prototypes.

## How to evaluate (review checklist for a fresh reader)

1. **Open the theme picker prototype** ([`docs/ui/prototypes/theme-picker.html`](ui/prototypes/theme-picker.html)). Click between the three themes. Confirm the design system handles dark contemporary, dark retro pixel, and light era-neutral with the same scaffold.
2. **Read the contract's posture section** ([`docs/community-resources.md`](community-resources.md) § Posture, § Avoiding the appearance of favoritism). Confirm the principles (community resource, plurality, transparency) hang together.
3. **Read one screen spec end-to-end** — recommend [`answer-with-citations.md`](ui/screens/answer-with-citations.md) (most central). Confirm it's implementation-ready (concrete tokens, all states specced, mobile/desktop, a11y).
4. **Cross-reference one rendered prototype against its spec** — pick `refusal-state.html` and compare to `answer-with-citations.md` § Refusal that directs out. Confirm the prototype matches what the spec promises.
5. **Read the outreach skill** (`~/.claude/skills/earlybirdsolutions-outreach/SKILL.md`) and one of the sent emails (in your EBS Gmail Drafts / Sent folder). Confirm voice / posture is consistent with the contract's principles.

## Path to implementation

When this branch graduates from "brainstorm" to "implementation," the order:

1. **Choose the framework** — per project phasing, Blazor is the locked frontend. The current prototypes are framework-agnostic HTML / CSS — porting to Blazor is translation, not redesign.
2. **Lock visual tokens into a shared CSS module / Razor design tokens.** The token values from `answer-with-citations.md` § Locked visual tokens are the source of truth.
3. **Build components in Modern LCD first.** Sibling themes are token swaps after the components exist.
4. **Implement screens in this order** (per surface importance):
   1. **Empty / landing** — cold-load entry, simplest, highest first-impression value
   2. **Answer-with-citations** — the central UX object
   3. **What-we-cover disclosure** — fulfills coverage-transparency promise
   4. **Refusal state** — most distinct visual moment; same panel slot as answer
   5. **Machine detail** — most complex, defer to last
   6. **Settings** — needed only after sibling themes ship and theme-picker becomes meaningful
5. **The contract** (`community-resources.md`) feeds into the AI agents' prompts and the routing / resolver code. UI consumes resolver output.
6. **The outreach skill** continues operating from `~/.claude/skills/`. Operator responses update `memory/project_pricing_outreach_2026_05_08.md` and may trigger directory entries promoting from link-only to first-party-with-attribution.

## Branch state

- **Branch:** `Dev-WebUiThemesBrainstorm` (worktree: `.claude/worktrees/peaceful-lewin-685cd6/`)
- **Off-branch shipped to `main`:** PR #133 (`docs/community-data-attribution.md`) — created from a separate worktree, no contamination of this branch
- **Uncommitted on this branch:** all spec docs, prototypes, contract additions, this handoff doc. **Nothing committed yet.**
- **Recommended commit strategy when ready:** one or two thematic commits — e.g., one for "design specs + prototypes" and one for "contract + memory + outreach" — rather than file-by-file. Or a single squash commit when ready to convert to PR.
- **Recommended PR strategy:** when this branch eventually graduates to a PR against `main`, frame it as "design system + community-resources contract + outreach tooling" — three logical groups in the PR description. The screen specs and prototypes are the design system; the contract is the routing layer; the outreach skill is operational tooling. Someone reviewing should be able to drill into any group independently.

## Iteration log

| Date | Change | Rationale |
|---|---|---|
| 2026-05-09 | v1 — handoff doc created | Brainstorm produced enough artifacts (4 screen specs + 1 theme system spec + 1 sibling overview + 6 working prototypes + a contract through 7 versions + an outreach skill + 4 sent emails + 5 memory entries created or updated + 1 shipped PR) that a single discoverable map became necessary for future sessions and implementation handoff. This doc is the bridge from "active brainstorm" to "implementation-ready seed." |
