# Brainstorm Handoff — `Dev-WebUiThemesBrainstorm`

> **Status (v2 — 2026-05-09):** **Graduated.** The brainstorm produced its design artifacts (committed at `a1d0717` / `c932bd5`) and the branch has since absorbed `main` twice — Wave 1 of Phase 5 User Delight is live in `src/PinballWizard.Web/`. This doc now serves as the *bridge* between the design intent and the in-flight implementation: it records what shipped, what drifted from the design, and what's still owed.
>
> **Started:** 2026-05-08. **Worktree:** `.claude/worktrees/peaceful-lewin-685cd6/`. Brainstorm tip: `5c4366b` (post-merge-with-main on 2026-05-09).
>
> **Single-paragraph framing (unchanged):** This branch designs PinballWizard's user-facing surface (web UI themes, screen specs, community-resource routing contract, operator-outreach tooling) before any frontend implementation begins. The thesis: get the *posture* right (community-resource, plurality, appearance-of-favoritism, citation-as-hero, polite-by-construction at the UI layer) before writing Blazor code, because the posture shapes every visual and structural decision. Once the design is right, the implementation is translation, not invention.
>
> **What changed since v1:** the implementation is no longer hypothetical. [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md) was accepted 2026-05-09 and locks the architectural layer beneath every brainstorm artifact (Blazor Web App auto-render, SSE streaming, dual `IAiRouter`, `AnswerChunk` discriminated union, MudBlazor-strict + custom-for-delight-surfaces, plural recovery payload, ProblemDetails degradation, `pinwiz.ai.first_token_ms`). Wave 1 PRs (#147 PR-C1 widen `Citation`, #152 PR-F1 layout shell) have already landed, putting `PinballTheme.cs`, `MainLayout.razor`, `WizardShell.razor`, `BrandHeader.razor`, and `TiltErrorBoundary.razor` in `src/PinballWizard.Web/`. The brainstorm is now the *design intent* against which the in-flight implementation is checked.

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

- **PR #133** — `docs: add plain-language community-data-attribution one-pager (for outreach replies)` — landed `docs/community-data-attribution.md` so the URL referenced (implicitly) by the outreach emails is live. Required deleting the `PERSONAL_IDENTITY_PATTERN` repo secret to unblock the sanitization scan. **Merged.** URL: <https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/133>

## What's landed in production code since v1 of this handoff

The brainstorm is no longer hypothetical. `Dev-WebUiThemesBrainstorm` has absorbed `main` twice (`55bf874`, `5c4366b`) and now carries the Phase 5 Wave 1 scaffold from those merges. Concretely:

| Artifact | Path | Origin | Notes |
|---|---|---|---|
| Phase 5 architectural ADR | [`docs/adr/0026-user-delight-frontend-and-streaming.md`](adr/0026-user-delight-frontend-and-streaming.md) | PR #146 | Locks Blazor Web App auto-render mode + SSE streaming + dual `IAiRouter` + `AnswerChunk` union + MudBlazor-strict-plus-custom-for-delight + plural recovery + ProblemDetails degradation + `pinwiz.ai.first_token_ms`. Five-layer enforcement (ADR + guardrails + CLAUDE.md invariant 14 + PR self-audit item 9 + `/local-review` category 12). |
| Cosmos for User Delight ADR | [`docs/adr/0025-cosmos-for-user-delight.md`](adr/0025-cosmos-for-user-delight.md) | PR #146 | Sibling lock; relevant here only because it consumes the **ADR-0025 slot the v1 handoff anticipated for the community-resource posture.** Promotion of the community-resource posture must therefore go to **ADR-0027** (next available slot). |
| Blazor brand theme | [`src/PinballWizard.Web/Components/Theming/PinballTheme.cs`](../src/PinballWizard.Web/Components/Theming/PinballTheme.cs) | PR-F1 (#152) | MudBlazor `MudTheme` factory. **Drift from Modern LCD spec — see drift table below.** |
| Public chrome layout | [`src/PinballWizard.Web/Components/Layout/MainLayout.razor`](../src/PinballWizard.Web/Components/Layout/MainLayout.razor) | PR-F1 (#152) | `MudThemeProvider` + `MudLayout` + `MudAppBar` (hosting `BrandHeader`) + `MudMainContent` wrapping `TiltErrorBoundary`. Dark-mode forced (`IsDarkMode="true"`). |
| Wizard container | [`src/PinballWizard.Web/Components/Wizard/WizardShell.razor`](../src/PinballWizard.Web/Components/Wizard/WizardShell.razor) | PR-F1 (#152) | Centered `MudContainer MaxWidth="MaxWidth.Large"`; layout partial, not a routable page. |
| Brand header | [`src/PinballWizard.Web/Components/Theming/BrandHeader.razor`](../src/PinballWizard.Web/Components/Theming/BrandHeader.razor) | PR-F1 (#152) | Text-only logo placeholder ("● PinballWizard"); nav links: Home / Wizard / About / Status. Anonymous-only routes — no admin link in public bar. |
| Tilt error boundary | [`src/PinballWizard.Web/Components/Theming/TiltErrorBoundary.razor`](../src/PinballWizard.Web/Components/Theming/TiltErrorBoundary.razor) | PR-F1 (#152) | Pinball-themed unhandled-render-exception fallback. Replaces ASP.NET framework default. |
| Citation DTO widening | [`src/PinballWizard.Application/Ai/Citation.cs`](../src/PinballWizard.Application/Ai/Citation.cs) + [`CitationSourceType.cs`](../src/PinballWizard.Application/Ai/CitationSourceType.cs) | PR-C1 (#147) | `Citation` now carries `PageStart`, `PageEnd`, `SectionHeading`, `SourceType`, nullable `RelevanceScore` + `LastScrapedUtc`. Backend half of the citation-as-hero contract from ADR-0026 § 8. |

### Drift between Modern LCD spec and shipped MudBlazor theme

The shipped Wave 1 theme is **spirit-aligned but token-unaligned** with [`docs/ui/themes/modern-lcd.md`](ui/themes/modern-lcd.md). This is the most consequential drift to surface for the implementation track:

| Token | Spec value (locked v3.3) | Shipped in `PinballTheme.cs` | Severity |
|---|---|---|---|
| `accent-primary` (amber) | `#ff9a1f` range | `#F5A623` | ⚠️ Close-but-not-identical. Both read as "arcade amber" but the spec's value is locked against the JJP-game reference; the shipped value is Material's `amber 700`. |
| `bg-base` (background) | `#0c0b0e` range (warm near-black) | `#121212` (Material's `dark.background`) | ⚠️ Spec calls for *warm* near-black ("not phone OLED"); shipped is neutral pure-grey. Reads colder than the spec intends. |
| `bg-surface` (panel) | `#161519` range | `#1E1E1E` | ⚠️ Same drift direction — neutral grey vs. warm-tinted. |
| `text-primary` | `#f4f1ea` range (warm off-white) | `#F0F0F0` | ⚠️ Same drift — clinical white vs. warm off-white. |
| Display font | Barlow Condensed 700 / 500 | Roboto (default) | 🔴 No display font shipped. The spec spike resolved Barlow against Saira / Oswald / Anton — none of that is in production yet. |
| Body font | Inter | Roboto | ⚠️ Roboto is acceptable but not spec. |
| Mono font | JetBrains Mono | (none specified) | ⚠️ Citations / metadata calls for mono; not yet wired. |
| `accent-grounded` (atomic green for citations) | `#34d96a` range | (Material `Success = #4CAF50`) | ⚠️ Color drift — spec value is the "match award lit" GI-glow green; shipped is Material's stock success green. |
| `accent-refusal` | `#ff3b30` range (saturated red) | (Material `Error = #F44336`) | ⚠️ Same drift direction — Material defaults vs. specified hues. |
| `accent-mode` (magenta for mode/topic) | `#e13bd9` range | (not present in palette) | 🔴 Mode/topic accent not yet defined in MudTheme. |

**Reading:** PR-F1 shipped the *scaffold* — the "make MudBlazor render *something*" Wave-1-foundational PR — using Material defaults plus arcade amber and dark mode. The Modern LCD spec's full token set has not yet been locked into the theme. This is consistent with PR-F1's stated scope ("Wave 1 ships the baseline; Wave 2 PR-D-degraded layers prefers-reduced-motion handling for pinball micro-interactions") — but it means the design system is not yet enforced in code, and Item 3 in the resume queue (drift audit) will need to produce a concrete PR proposing the spec-aligned token swap.

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
- **Sibling theme prototypes for Cabinet and Score Reel** — sketched in `sibling-themes-overview.md` but not yet rendered. (Backbox shipped 2026-05-08 in [`answer-with-citations-backbox.html`](ui/prototypes/answer-with-citations-backbox.html); the v1 open-question list contradicted its own prototype table.)
- **Display font for Daytime Route** — Barlow (open) is the working pick; could spike against DM Sans Bold.
- **Theme-picker placement:** prototyped at top-of-page; locked to "lives on Settings screen" for production. Settings screen spec doesn't exist yet.
- **NEW: Modern LCD spec → MudTheme token-alignment PR.** The drift table above documents the gap between the locked spec and what `PinballTheme.cs` shipped. A focused PR — branch e.g. `Dev-Phase5ModernLcdTokenAlignment` — that swaps Material defaults for the spec values (warm amber, warm off-white, atomic green, mode magenta) and adds Barlow Condensed + Inter + JetBrains Mono via `App.razor` head links would close the gap. Sequence: ship after the foundational chrome stabilizes (PR-F2), before Wave 2 delight surfaces lock against the wrong tokens.

### Contract
- **Question-topic dominance heuristic** for multi-topic questions (single-topic-per-question is locked; need an explicit rule for picking the dominant one).
- **Validation batch infrastructure** for link health (Pinside specifically blocks programmatic UAs — needs alternative validation strategy). Note that ADR-0026 § 7 commits Phase 5 to a CI URL-liveness check on the community-resource seed JSON; that infrastructure should subsume this.
- **ADR promotion (slot revised).** ADRs 0025 (Cosmos for User Delight) and 0026 (User Delight Frontend + Streaming) consumed the slots the v1 handoff anticipated; the community-resource posture promotion now goes to **ADR-0027**. Doc has stabilized through `community-resources.md` v1.7; ADR draft is item 2 in this resume queue. Requires explicit user confirmation before commit.

### Outreach
- **Operator responses** to the 4 sent emails. Response-handling guidance in `memory/project_pricing_outreach_2026_05_08.md`.
- A "yes" from any operator promotes their data from link-only to first-party-with-attribution; a "no" or no-response keeps things as they are.

### Visual / rendering
- **What reads off in the prototypes** — token values are committed but no real-eyes critique pass yet. Iteration may follow once you've spent time in the rendered prototypes.
- **NEW: Cross-check the Phase 5 chrome against the screen specs.** [`MainLayout.razor`](../src/PinballWizard.Web/Components/Layout/MainLayout.razor), [`WizardShell.razor`](../src/PinballWizard.Web/Components/Wizard/WizardShell.razor), and [`BrandHeader.razor`](../src/PinballWizard.Web/Components/Theming/BrandHeader.razor) need to be audited against the locked screen specs (especially [`empty-landing.md`](ui/screens/empty-landing.md) and [`answer-with-citations.md`](ui/screens/answer-with-citations.md) for chrome expectations: brand-header treatment, no-engagement-metric framing in nav, citation-as-hero on the Wizard surface). This is item 3 in the resume queue.

## How to evaluate (review checklist for a fresh reader)

1. **Open the theme picker prototype** ([`docs/ui/prototypes/theme-picker.html`](ui/prototypes/theme-picker.html)). Click between the three themes. Confirm the design system handles dark contemporary, dark retro pixel, and light era-neutral with the same scaffold.
2. **Read the contract's posture section** ([`docs/community-resources.md`](community-resources.md) § Posture, § Avoiding the appearance of favoritism). Confirm the principles (community resource, plurality, transparency) hang together.
3. **Read one screen spec end-to-end** — recommend [`answer-with-citations.md`](ui/screens/answer-with-citations.md) (most central). Confirm it's implementation-ready (concrete tokens, all states specced, mobile/desktop, a11y).
4. **Cross-reference one rendered prototype against its spec** — pick `refusal-state.html` and compare to `answer-with-citations.md` § Refusal that directs out. Confirm the prototype matches what the spec promises.
5. **Read the outreach skill** (`~/.claude/skills/earlybirdsolutions-outreach/SKILL.md`) and one of the sent emails (in your EBS Gmail Drafts / Sent folder). Confirm voice / posture is consistent with the contract's principles.

## Path to implementation (revised — implementation is in flight)

The graduation already happened. The path below reflects what's *actually shipping*, not what *would* ship if the brainstorm graduated. Per [ADR-0026](adr/0026-user-delight-frontend-and-streaming.md), Phase 5 User Delight runs across 4 waves with ~24 PRs:

1. **✅ Wave 0 — ADRs + 5-layer enforcement (PR #146).** Done. ADR-0025 (Cosmos for User Delight) + ADR-0026 (Frontend + Streaming) accepted; CLAUDE.md invariant 14, PR self-audit item 9, `/local-review` category 12 all wired.
2. **🟡 Wave 1 — foundational, in progress.** Backend foundational: PR-R1 (RefusalDetail), **PR-C1 (Citation widening — #147 ✅)**, PR-D1 (DegradationContext on `WizardAnswer`), PR-S1 (`AnswerChunk` discriminated union). Frontend foundational: PR-F0 (project skeleton), **PR-F1 (layout shell — #152 ✅)**, PR-F2 (chrome polish). Backend track ║ Frontend track — file-disjoint, parallelized across worktrees.
3. **⏳ Wave 2 — delight surfaces.** The four custom components per ADR-0026 § 6: `WizardAnswerStream` (streaming), `RefusalPanel` (recovery), `CitationCard` / `CitationGroup` / `CitationStrip` (provenance), `TiltPage` / `TiltErrorBoundary` (degradation, partial — boundary already shipped in PR-F1). This is where the screen specs in [`docs/ui/screens/`](ui/screens/) become directly translated.
4. **⏳ Wave 3 — finishing.** Lighthouse + axe-core gates, the `pinwiz.ai.first_token_ms` instrument, the landing endpoint + `featured_machines` Cosmos lookup, the warmup hosted service.

### Where the brainstorm artifacts land in the implementation

| Brainstorm artifact | Phase 5 destination | Status |
|---|---|---|
| Modern LCD theme spec | `PinballTheme.cs` token values | 🟡 Drift exists (see drift table). Token-alignment PR pending — see open questions. |
| Sibling theme system (CSS-variable token swap) | MudBlazor's `MudThemeProvider` w/ multiple `MudTheme` instances + a Settings-screen picker | ⏳ Not started. Sibling themes are Wave 3 / post-Wave-3; only Modern LCD is in flight. |
| Answer-with-citations screen spec | `WizardAnswerStream` + `CitationCard` / `CitationGroup` / `CitationStrip` (Wave 2) | ⏳ |
| Empty / landing screen spec | `/api/wizard/landing` + `Pages/Index.razor` (Wave 3) | ⏳ |
| What-we-cover disclosure spec | `/about` route (anonymous) | ⏳ |
| Refusal state prototype | `RefusalPanel` consuming `WizardAnswer.RefusalDetail` from PR-R1 | ⏳ Backend half (RefusalDetail DTO) is PR-R1; frontend half is Wave 2. |
| Machine detail screen spec | Wave 3+ — most complex, deferred per spec | ⏳ |
| `community-resources.md` contract | `data/seeds/community_resources.v1.json` consumed by `RefusalPanel` (ADR-0026 § 7) | ⏳ Seed format / CI URL-liveness check pending. |
| Outreach skill + sent emails | `~/.claude/skills/earlybirdsolutions-outreach/` (operational, not in-product) | ✅ Live; awaiting operator responses. |

### Cross-cutting commitments still owed

- **Token alignment PR** (Modern LCD spec → `PinballTheme.cs`) — see drift table above. Should ship before Wave 2 components calcify against Material defaults.
- **ADR-0027 community-resource posture** — promotion of the contract's posture into a discoverable architectural decision. Doc through v1.7 is implementation-ready; ADR is the durable record.
- **Sibling theme prototypes** for Cabinet and Score Reel + a **Settings screen spec** that hosts the theme picker.
- **Real-eyes critique pass** on the rendered prototypes — token values are committed but unvalidated against actual viewing.

## Branch state

- **Brainstorm branch:** `Dev-WebUiThemesBrainstorm` at `5c4366b` (post-merge with `main` on 2026-05-09; second main-merge of the branch's life). All design artifacts are committed at `a1d0717` (specs + prototypes) and `c932bd5` (contract + handoff index). v1's "nothing committed yet" line is no longer accurate.
- **Worktree:** `.claude/worktrees/peaceful-lewin-685cd6/` — sole owner of the brainstorm branch.
- **Resume-work branch:** `Dev-WebUiBrainstormResume` (created 2026-05-09 off `5c4366b` for this v2 handoff + the resume queue). Keeps the brainstorm branch frozen-in-time and avoids overlap with the concurrent `main`-lane session.
- **Concurrent session lane:** the main checkout at `C:/projects/PinballWizard/` is on `main` (tip `30e58ca`). It owns the Phase 5 Wave 1 PR series (#147, #152, etc.). This worktree never touches `main`.
- **Off-branch shipped to `main`:** PR #133 (`docs/community-data-attribution.md`) — created from a separate worktree, no contamination of this branch. **Merged.**
- **Recommended PR strategy when graduating remaining brainstorm artifacts:** the design system, contract, and outreach tooling are now best landed via *focused* PRs against `main` (one per logical unit) rather than as a single brainstorm-omnibus PR — Phase 5 is in flight and small atomic PRs interleave better with the Wave 1/2/3 sequence. Token-alignment PR, ADR-0027 PR, sibling-theme-prototype PR, Settings-screen-spec PR.

## Iteration log

| Date | Change | Rationale |
|---|---|---|
| 2026-05-09 | v1 — handoff doc created | Brainstorm produced enough artifacts (4 screen specs + 1 theme system spec + 1 sibling overview + 6 working prototypes + a contract through 7 versions + an outreach skill + 4 sent emails + 5 memory entries created or updated + 1 shipped PR) that a single discoverable map became necessary for future sessions and implementation handoff. This doc was the bridge from "active brainstorm" to "implementation-ready seed." |
| 2026-05-09 | v2 — handoff reconciled with implementation reality | Branch absorbed `main` twice the same day, picking up Phase 5 Wave 0 (ADRs 0025 + 0026) and Wave 1 foundational PRs (PR-C1 #147, PR-F1 #152). v2 adds: (a) "What's landed in production code" section mapping shipped artifacts; (b) drift table comparing Modern LCD spec tokens to `PinballTheme.cs` reality (warm amber/`#ff9a1f` vs. Material `#F5A623`; warm near-black/`#0c0b0e` vs. neutral `#121212`; Barlow Condensed vs. Roboto; Inter vs. Roboto; missing JetBrains Mono and atomic-green/mode-magenta accents); (c) ADR-slot revision (community-resource posture is now ADR-0027, since 0025/0026 were claimed by the User Delight tracks); (d) revised path-to-implementation reflecting Wave 0/1/2/3 sequence; (e) brainstorm-artifact → Phase 5 destination map; (f) cross-cutting commitments still owed (token alignment PR, ADR-0027, Cabinet/Score Reel siblings, Settings spec, real-eyes critique). Branch state corrected (v1's "nothing committed yet" was obsolete by commit time). |
