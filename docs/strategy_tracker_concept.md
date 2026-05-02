# Strategy Tracker — Phase 5+ Digital Passport Module (Concept Spec)

> **Status:** Documented for future implementation. Ships as a **headline
> module of Digital Passport** in Phase 5+. Currently Passport itself is
> deferred-to-v2 in the locked architecture; Strategy Tracker is a strong
> reason to consider promoting Passport's first module to ship alongside
> the public Blazor launch.
>
> **Decision recorded:** 2026-05-02. See project memory
> `project_strategy_tracker_concept.md` for the locked scope discipline.

## What it is

A structured strategy + outcome tracker for **competitive and serious
players** — the IFPA-ranked / Match Play / Pro Circuit / location
tournament audience. Lets a user:

- **Build a strategy library**: per-machine, per-edition strategies with
  named entries, target shots, mode priority, expected outcomes
- **Log game sessions** against those strategies: which strategy was used,
  final score, achievements (modes qualified, multiballs reached), free-form
  observations
- **Version strategies**: "Stranger Things Pro v3 — adjusted ball-locking
  sequence after 12 sessions"
- **See analytics**: per-strategy median/best score, mode-success rates,
  which strategies work best on which machine/edition over time
- **Get AI-assisted refinement** (optional): the Wizard reviews user data
  + game rules from the corpus and suggests grounded adjustments

**Worked example.** A competitive player who plays the Stranger Things Pro
at their local on Wednesdays creates two strategies:

- "ST Pro Speed — qualify Demogorgon Multiball ASAP, ignore modes"
- "ST Pro Modes — light all 4 modes, then start Demogorgon for stack"

After 20 sessions logged against each, the analytics view shows Speed
yields a higher median (8.2M vs 6.4M) but Modes yields a higher ceiling
(34M peak vs 18M peak). The Wizard, given access to both data sets and
the rule corpus, surfaces: "Your Modes strategy underperforms because
you're starting Demogorgon Multiball before lighting Slugger 83% of the
time. The rules show Slugger gives 2x jackpot multiplier. Reordering to
light Slugger first should close the median gap." Each Wizard claim cites
both the rule from the manual and the user's own data.

## Why it fits the architecture

Strategy Tracker reuses every piece of planned infrastructure with **no
new architectural surface**:

| Existing planned feature | Reuse |
| --- | --- |
| Cosmos Serverless (NoSQL) | New containers `strategies`, `game_sessions`, partitioned by user |
| Entra External ID end-user auth | Same auth path as Passport / scores |
| AI Search + Wizard router | Refinement uses the same retrieval + completion pipeline |
| OCR score capture (planned) | Photo → Vision LLM extracts score → "which strategy were you using?" → auto-creates session log |
| Match Play / IFPA APIs (planned, deferred) | Tournament results auto-import into game session log |
| Provenance ethos | Wizard refinement output cites both rules-from-corpus AND user's own data |

This is the "this feature multiplies the value of every other planned
feature" property — Strategy Tracker is what makes the OCR pipeline and
the tournament-API integrations actually compelling rather than nice-to-have.

## Sequence dependency (this is the real constraint)

Strategy Tracker is **half-built without the auto-data hooks**. Manual
logging alone will lose to the spreadsheets and notebooks that competitive
players already use today. Adoption depends on:

1. **OCR score capture working end-to-end** (camera → Vision LLM → score +
   machine identification → session log entry pre-populated)
2. **At least one tournament API integrated** (Match Play first; IFPA
   later) to auto-import tournament play sessions

Without those hooks, the data-entry friction kills the feature. Order of
operations: ship OCR first, ship Match Play integration second, ship
Strategy Tracker on top of both.

## Tiered logging (the friction-reduction model)

Three tiers of session entry:

- **Quick log** (two taps): score + strategy ID + thumbs up/down
- **Detailed log** (opt-in): mode-by-mode breakdown, multiball qualification
  count, observations, photos
- **Auto-log** (zero-touch): Match Play / IFPA API import; OCR score
  photo capture pre-fills score + machine + edition

Most sessions land at quick-log. Detailed log is for sessions worth
post-mortem. Auto-log is the scale path.

## Scope discipline (locked)

Strategy + Outcome + Analytics + AI-assisted refinement. Nothing else in
v1 of this module. Tempting expansions to **defer**:

| Expansion | Why deferred |
| --- | --- |
| **General pinball journal** (location logs, "I played a round at X bar last night") | Different feature, broader audience, scope-creep risk |
| **Public strategy sharing / social network** | Privacy, moderation, IP risks; not the core need; v3+ at earliest |
| **Coaching marketplace** (paid sessions with top players) | Payments, KYC, tax — entirely different product |
| **Achievement / badge system** | Game-ification adjacent; nice but not core |
| **Live coaching during play** (real-time AI overlay) | Requires AR / device integration; v∞ |

Discipline here is what keeps the feature ship-able. Every one of those
expansions is a reasonable thought; all of them are no for v1.

## Cost / IP feasibility

- **Cost: trivial.** Cosmos storage is kilobytes/user. Cosmos RU is
  modest at hobby scale (per-user partitioning + sensible indexing keeps
  query cost low). AI assist is text-gen at ~$0.05–0.20 per refinement
  request. **Zero image generation.** Fits comfortably inside the $400/mo
  cap with room to spare.
- **IP: near-zero exposure.** User-generated content about user's own
  play. Standard ToS sufficient. Single watch-out: restrict photo uploads
  to score-display photos to avoid users uploading copyrighted
  tournament-stream screenshots. UI affordances + ToS handle this.

## Cosmos data model sketch

This is forward-looking, not a build spec — actual schema decisions wait
for Phase 5 design.

```
strategies (container, partition: /userId)
  {
    id: "strat_<ulid>",
    userId: "<entra-external-id>",
    machineOpdbId: "GRBN-MQR4P",
    edition: "Pro",
    name: "ST Pro Speed",
    version: 3,
    description: "Qualify Demogorgon Multiball ASAP; ignore modes.",
    targetShots: ["lock-left-ramp", "demogorgon-loop"],
    modePriority: ["demogorgon-mb"],
    notes: "...",
    createdAt, updatedAt, isActive
  }

game_sessions (container, partition: /userId)
  {
    id: "sess_<ulid>",
    userId: "<entra-external-id>",
    machineOpdbId: "GRBN-MQR4P",
    edition: "Pro",
    strategyId: "strat_<ulid>",
    strategyVersion: 3,
    finalScore: 8214670,
    achievements: { modesQualified: 0, multiballsReached: 1, slugger: false, ... },
    sentiment: "thumbs-up" | "thumbs-down" | null,
    observations: "...",
    source: "manual" | "ocr" | "match-play" | "ifpa",
    sourceRefId: "...",
    playedAt
  }
```

Analytics views are computed from `game_sessions` aggregations — no
separate denormalized rollup container needed at v1 scale (revisit if RU
cost climbs).

## Refinement loop (the AI-assist piece)

User clicks "Wizard, review my Stranger Things Pro Speed strategy."
Backend:

1. Pulls last N sessions for that strategy from Cosmos (partitioned by
   user — fast)
2. Pulls the Machine + Edition rule chunks from AI Search (same retrieval
   as Wizard Q&A)
3. Prompts the Wizard router with both: "Given this user's outcomes on
   Strategy X over N sessions [data summary] and the following rule
   excerpts [chunks], suggest 1–3 specific adjustments. Each suggestion
   must cite (a) which rule supports it and (b) which data pattern from
   the user motivated it."
4. Returns a structured response: list of suggested adjustments, each
   with rule citation + data citation
5. UI lets user accept (creates strategy v+1 with suggested change) or
   dismiss

**Provenance angle:** every refinement cites both the manual and the
user's own data. That's the on-brand version — the citation ethos
generalizes from "rules from the manual" to "rules + the user's own
empirical evidence."

## Implementation sketch (when Phase 5 arrives)

- **Application layer:** new use cases in `PinballWizard.Application/`:
  `CreateStrategyAsync`, `LogGameSessionAsync`, `GetStrategyAnalyticsAsync`,
  `RefineStrategyAsync` (Wizard-assisted)
- **Infrastructure:** Cosmos repos (`StrategyRepository`,
  `GameSessionRepository`); Match Play / IFPA HTTP clients (when those
  integrations land); reuse OCR pipeline for photo-based session capture
- **Frontend (MudBlazor):**
  - `/passport/strategies` — `MudDataGrid` of user's strategies, per-machine
    grouping, version history
  - `/passport/strategies/{id}` — strategy detail + session list +
    analytics charts (`MudChart`)
  - `/passport/sessions/log` — quick-log form (score + strategy + thumbs);
    detailed-log expanded view; OCR photo upload
  - `/passport/strategies/{id}/refine` — Wizard refinement with
    structured suggestion cards
- **Auth:** all routes require Entra External ID (same as Passport)

## Out of scope for v1 of this module

See "Scope discipline" table above. The compressed version: ship the
core loop (build strategy → log sessions → see analytics → optional AI
refinement) and stop. Every expansion is a v3+ conversation.

## Open questions for Phase 5 design discussion

1. Does Strategy Tracker ship with the public Blazor launch (promoting
   Passport's first module from v2) or wait for full Passport v2?
2. OCR + Match Play integrations are sequence dependencies — what's the
   minimum-viable subset? (Suggest: OCR is required, tournament-API is
   nice-to-have-on-launch.)
3. Cosmos partition strategy: single `/userId` partition for both
   strategies and sessions, or separate by entity? (Suggest: same
   partition — most queries are per-user.)
4. Analytics: real-time Cosmos aggregations on every page view, or
   precomputed nightly rollups in a `strategy_analytics` container?
   (Suggest: real-time at v1 scale; revisit if RU cost climbs.)
5. Strategy import/export (JSON / CSV) for users who already keep
   spreadsheets? (Suggest: yes, low-effort win for adoption.)
