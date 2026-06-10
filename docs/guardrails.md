# Guardrails

The meta-spec for PinballWizard. This document explains *how decisions get made and how scope gets defended* across the lifetime of the project — the rules of engagement that govern every PR, every phase, every decision. It is the doc that detects drift from the vision.

Read alongside [`vision.md`](vision.md) (what we're building), [`build-spec.md`](build-spec.md) (the comprehensive plan), and [`quality-spec.md`](quality-spec.md) (the quality gates).

When something in this doc conflicts with another doc, this one wins. When something here conflicts with a decision in CLAUDE.md or memory, surface the conflict explicitly before acting.

## The seven main goals

Every guardrail in this doc exists to keep these seven goals in alignment. If a proposed action doesn't advance at least one, it is presumptively out of scope.

1. **Showcase outcome.** A prospect can land on the repo and/or `pinwiz.ai`, in 10 minutes, form a confident view that Earlybird Solutions can architect, build, ship, and operate enterprise-class AI solutions.
2. **Quality bar.** Every artifact (code, test, doc, infra, commit, PR, deploy) holds up under senior-IC and senior-architect scrutiny.
3. **Cost ceiling.** $300–$400/month steady-state, anomaly alarm at $300. Implemented at the infrastructure level via the two-tier Bicep deploy gate per [ADR-0013](adr/0013-two-tier-bicep-deploy.md).
4. **Politeness invariants.** Non-negotiable, visibly enforced in code, never traded for performance or completeness.
5. **Provenance.** Every Wizard answer carries clickable citations end-to-end; no decoupling between answer and source.
6. **Personal-account constraint.** Never linked to work tooling, identity, data, or infrastructure. Personal Earlybird Azure subscription only; personal-noreply git identity only.
7. **Operability.** The system runs healthy without intervention; when it doesn't, alerts route somewhere and runbooks exist.

## Scope discipline

### Feature triage

Every feature idea — whether from the user, a memory note, an `/local-review` ⚠️ finding, or Claude's own suggestion — lands in exactly one of three buckets. State the bucket explicitly when triaging.

| Bucket | Criteria | Where it lives |
| --- | --- | --- |
| **In-scope, scheduled** | Advances ≥ 1 of the seven goals; fits the budget; sequenced into a phase | `build-spec.md` under the phase that owns it |
| **Deferred** | Defensible idea, not advancing v1 launch readiness, or budget/scope cost too high *now* | `build-spec.md` § "Deferred features" with revisit trigger noted |
| **Dropped** | Doesn't advance the goals, conflicts with a locked decision, or net-negative for the showcase | Decision log entry explaining *why dropped* |

**Default to deferred over in-scope.** A feature added late is cheaper than a feature shipped half-built. The bias is against expansion.

### Scope-creep refusals

These specific patterns must be refused, not absorbed:

- **"While I'm in there..."** — secondary changes that ride along with a primary PR. Each PR has one purpose; secondary work goes in its own PR.
- **"It's a small addition..."** — small features compound. If it isn't in the build-spec for the current phase, it isn't in this PR.
- **"We can polish it later..."** — quality is per-PR, not per-release. A "polish later" item is a backlog hazard, not an acceptable shortcut. Either ship to bar or don't ship.
- **"Let's just match what other tools do..."** — feature parity with another product is not a reason to add a feature. Goal alignment is.
- **"This is what enterprise apps usually have..."** — generic enterprise checklists are not a substitute for goal-specific scope. Each gate, each integration, each component must be justified against the seven goals.

When refused, the idea goes to the deferred-features list with a note. Don't lose ideas — defer them.

### Locked decisions

Some decisions are explicitly **not relitigated** without surfacing the change. The current list (replicated from CLAUDE.md and memory):

- ARM for Cosmos schema CRUD; data-plane SDK for item CRUD. Containers not in Bicep. ([ADR-0012](adr/0012-cosmos-arm-schema-data-plane-items.md))
- AI Search Basic + Cosmos Serverless (not pgvector, not Postgres, not AI Search Standard).
- MudBlazor strict (not MUI, Radzen, Syncfusion, hand-rolled).
- Microsoft Entra External ID for both admin RBAC (v1) and end-user social login (when passport ships).
- Personal Earlybird Azure subscription only; personal-noreply git identity.
- Robots.txt honored unconditionally — sites declaring `Disallow: /` are skipped without a polite-outreach grant.
- Provenance is sacred; any data path that drops `Source` / `DiscoveryUrl` / `DiscoveryContext` / `GameSlug` is a 🔴.
- Cosmos partition keys per ADR 0011; no per-PR re-decisioning.
- Microsoft Foundry orchestration (Microsoft Agent Framework + `Azure.AI.Projects` 2.0 GA) — Responses Agent pattern, function tools for catalog grounding, OTel auto-emission. Reference architecture for client engagements. ([ADR-0014](adr/0014-microsoft-foundry-orchestration.md))
- Per-`AIAgent` model selection: gpt-4o-mini default for Wizard / Valuation / Rules; gpt-4.1 for Repair + escalation tier. In-process LRU semantic cache (~512 entries, prompt-version-keyed). Per-call cost ceiling enforced. ([ADR-0015](adr/0015-cost-routing-and-semantic-cache.md))
- Confidence-threshold refusal mandatory — geometric-mean composite of (retrieval similarity, model self-reported, citation coverage); below-threshold returns a categorized refusal rather than fabricating. Threshold default 0.65. ([ADR-0017](adr/0017-confidence-threshold-refusal.md))
- Code-resource agent definitions (Markdown prompts as `<EmbeddedResource>` in the Application csproj, constructed via `AIProjectClient.AsAIAgent`) — NOT Foundry portal prompt flow. Diffability + reviewability + atomic prompt+code commits. ([ADR-0018](adr/0018-prompt-management.md))
- **Hybrid chunking locked** — token-budgeted (~512 tokens, ~10% overlap) within heading-bounded sections; PdfPig outline as section delimiter with no-outline fallback to fixed-size windowing; page numbers + section heading preserved as chunk metadata. Citations resolve as `manual.pdf p.N–M`. ([ADR-0019](adr/0019-hybrid-chunking.md))
- **Embedding model confirmed** — `text-embedding-3-large` @ 3072d. NOT `ada-002`; NOT `text-embedding-3-small`. Per-call cost: ~$0.13/1M tokens. ([ADR-0020](adr/0020-embedding-model.md))
- **AI Search index schema locked** — `pinwiz-rag-v1`; HNSW vector field `content_embedding` (3072d); semantic ranker enabled; faceted fields `manufacturer`, `machine_title`, `document_type`, `page_number`, `section_heading`; schema-breaking changes spin up `v2` with dual-read during cutover (never in-place). ([ADR-0021](adr/0021-ai-search-index-schema.md))
- **Tool-call-trace citation extraction locked** — citations come from `searchCorpus` / `getMachineByTitle` tool-call result trace inspection on `AgentResponse`, NOT from regex over agent prose. `ToolTraceCitationExtractor` is the canonical impl; the regex extractor is deleted. ([ADR-0022](adr/0022-citation-extraction.md))
- **Citation-required guardrail mandatory** — when zero citations attach to an answer, refuse with category `NoCitation` rather than answer. Structurally enforced in `AiRouter.ApplyPostAgentGuardrailsAsync`; cannot be soft-bypassed via prompt changes. Calibration can widen the extractor but cannot remove the gate without an ADR successor. ([ADR-0023](adr/0023-citation-required-guardrail.md))
- **Two-stage re-ranking strategy locked** — AI Search semantic ranker is Phase 4 v1; Cohere Rerank cross-encoder is the locked-path second stage, implementation deferred behind the H3 quality gate (`citation_precision < 0.65` AND ≥30% retrieval-side refusals). Evaluate the gate after Phase 4.5 eval-set realignment. ([ADR-0024](adr/0024-two-stage-reranking.md))
- **Cosmos client options posture**: Session consistency + Direct connection mode + `AllowBulkExecution=true` + `EnableContentResponseOnWrite=false` + per-host `ApplicationName`. Single-region East US 2 with documented revisit triggers (multi-region defers until user-geography signal). ([ADR-0025](adr/0025-cosmos-for-user-delight.md))
- **Selective indexing default for write-heavy Cosmos containers**: `rag_leases`, `rag_index_state`, `rag_dead_letters` use selective indexing policies that exclude unused fields. Default-indexing-everything is the right choice ONLY for containers with read-side query patterns still being tuned (`machines`, `ingestion_sources`, `scraped_documents`). ([ADR-0025](adr/0025-cosmos-for-user-delight.md))
- **Point-read over cross-partition for hot-path queries**: any Cosmos query on the user-facing critical path MUST be a point read. Cross-partition queries require an explicit RU-cost justification in the PR description AND a documented latency budget; absent both, the PR adds a materialized-view container instead. ([ADR-0025](adr/0025-cosmos-for-user-delight.md))
- **Wizard streaming transport posture**: Server-Sent Events (`text/event-stream`) over the public `/api/wizard/ask:stream` endpoint. NOT WebSocket, NOT SignalR, for the anonymous read surface — bidirectional needs that arise later go to SignalR per the documented revisit trigger. Streaming chunks travel as `AnswerChunk` discriminated-union JSON, never raw text deltas. ([ADR-0026](adr/0026-user-delight-frontend-and-streaming.md))
- **MudBlazor strict + custom for the four delight surfaces**: MudBlazor remains the only chrome library per [ADR-0008](adr/0008-mudblazor-strict.md). Custom components are permitted ONLY for the four delight surfaces — `WizardAnswerStream`, `RefusalPanel`, `CitationStrip` family, `TiltPage`/`TiltErrorBoundary` — with scoped CSS. New custom components outside that set re-litigate ADR-0008 and require surfacing. ([ADR-0026](adr/0026-user-delight-frontend-and-streaming.md))
- **Refusal supersedes prior `TextDelta` chunks** (streaming UX rule): a `Refusal(category, detail)` chunk arriving mid-stream invalidates every `TextDelta` already rendered; the client discards the partial answer and shows the refusal payload. Wire-protocol-level convention; tested via `AnswerChunkRefusalSupersedesTests`. ([ADR-0026](adr/0026-user-delight-frontend-and-streaming.md))
- **Community-resource posture — outbound is a feature, never editorialize.** PinballWizard routes users outward to the venues that own the answer. No editorial ranking ("we recommend X"), no engagement-metric framing (no trending / popular / signup gate / first-run tour / session-history surface), no captive UI patterns. Refusals direct out by naming what's missing and routing to a community resource that can answer. Aggregate outbound-click telemetry is acceptable for revisit-trigger evaluation; per-user click trails are forbidden. ([ADR-0027](adr/0027-community-resource-posture.md))
- **Destination plurality — alphabetical within plural sets, threshold per category surface.** Plural sets render alphabetically by display name (resolver-computed, never baked into the seed JSON). Plurality thresholds: ≥3 venues for marketplace recovery payloads, ≥2 venues for machine-database / forum / tool / location refusals, n/a for first-party manufacturer pages (singular by construction). Single-CTA refusals are forbidden for any non-singular category — if a curation gap exists, the resolver returns a category-elevated refusal naming the gap rather than promoting one venue. Visual parity is mandatory: identical card grammar, identical CTA weight across siblings. No "primary" CTA elevated above peers. ([ADR-0027](adr/0027-community-resource-posture.md))
- **Closed `QuestionTopic` 6-value enum + curated refusal-routing matrix.** `QuestionTopic` is `{Repair, Gameplay, Market, Location, Tournament, General}` — exactly six values. Adding a topic value requires amending [ADR-0027](adr/0027-community-resource-posture.md), which ripples through the matrix, the seed JSON, the agent prompts, and the contract tests. Soft-adding via a `RefusalPanel` edge case is forbidden. The refusal-routing matrix is `(RefusalCategory × QuestionTopic) → IReadOnlyList<CommunityResource>`, curated in `data/seeds/community_resources.v1.json` and pinned by `RefusalRoutingMatrixContractTests`. Pinside slug-resolution requires a hand-curated alias table at `data/seeds/pinside_slug_aliases.v1.json` — Pinside blocks programmatic UAs, so probing-at-runtime is forbidden. ([ADR-0027](adr/0027-community-resource-posture.md))

To relitigate any of these: stop work on whatever surfaced the conflict, write a one-paragraph "why this is being reopened" message to the user, and **wait for an explicit yes before proceeding**. Do not soft-erode a locked decision through a series of small concessions.

## Decision framework

### Autonomous vs. surface-to-user

Per `c:\projects\CLAUDE.md` § "Executing actions with care" and the seven main goals.

| Action class | Posture |
| --- | --- |
| Code edits, test additions, refactors with bounded blast radius | Autonomous, in Auto Mode |
| Adding/removing dependencies | Autonomous if minor version + green tests; surface for major version, new transitive surface, or unfamiliar package |
| Schema or data-model changes | Surface — even small ones break downstream code that lives in the spec |
| ADR additions | Autonomous to draft; **always surface and wait for explicit user confirmation before committing** — ADRs are append-only history and a wrong one is expensive to reverse |
| Spec doc updates (vision / build-spec / quality-spec / guardrails) | Surface a draft, get explicit yes, then commit |
| Infrastructure changes (Bicep, ACA Job config, Cosmos containers, RBAC) | Surface — these have cost and security blast radius |
| Deploys to dev / live | Surface — deploys are not reversible without effort |
| Anything touching the live demo or DNS | Surface — public surface |
| Secrets, identity, or auth config changes | Surface — security blast radius |
| Anything that could leak the work account or work data | Refuse and surface — see goal 6 |

**Bias toward surfacing in ambiguity.** The cost of a confirmation ping is low; the cost of an unwanted action on a showcase repo is high.

### When to invoke heavyweight review

| Trigger | Tool |
| --- | --- |
| Any PR touching auth, identity, secrets, or user input | `/security-review` |
| Cross-cutting refactor (≥ 3 layers, ≥ 5 files outside one feature directory) | `/ultrareview` (user-triggered; recommend it explicitly) |
| Phase boundary | This guardrails doc's phase-gate checklist + `/local-review` against the cumulative diff |
| Pre-public-launch | Operational readiness review (Phase 6 spec) |

`/local-review` and the 7-item self-audit run on **every** non-trivial PR — not just the heavyweight cases.

## Phase gates

Phase gates are the structural guardrail against drift across longer time horizons. The single-PR audit catches local issues; the phase gate catches cumulative ones.

### Per-PR gate (existing — recapitulated for context)

Already enforced via `/local-review` + 7-item self-audit + PR template. Brief recap:

1. `/local-review` (10-category qualitative, all 🔴 fixed, ⚠️ fixed-or-deferred-with-justification)
2. 7-item mechanical: dead-config grep, sibling-diff, no bare catch, CLI/orchestrator wiring, behavior-not-structure tests, zero warnings, identity check
3. Build green, tests green
4. Memory updated if anything new is locked
5. PR description records the audit outcome

### Per-phase gate

A phase is "complete" only when all of:

- [ ] All items in the phase's `build-spec.md` § Scope are shipped or explicitly deferred (with deferral noted in the same doc)
- [ ] Phase exit criteria from `build-spec.md` § Exit criteria all check
- [ ] The demonstrable artifact named in the phase spec exists and works
- [ ] All ADRs the phase generated are committed
- [ ] All quality gates in `quality-spec.md` applicable to this phase are green
- [ ] CLAUDE.md updated if new locked invariants emerged
- [ ] Risk register reviewed; risks resolved or rolled forward with current mitigation
- [ ] Decision log entries for any non-trivial sub-ADR-threshold decisions
- [ ] Cost-burn snapshot taken; under budget
- [ ] **README.md and `docs/vision.md` per-phase-close review** — every customer-facing claim is checked against the phase's actual deliverables. No aspirational language for shipped features; no overclaim of capability that the H-test data doesn't support; new known-limitations entries added if the phase exposed them. *(Lesson: post-Phase-3-close audit (PR #94) caught README/vision overclaims that PR-time review missed — twice running. Per-phase audit catches what per-PR audit misses.)*
- [ ] Memory handoff written so the next session can resume cleanly

Phase exit is a single user-confirmed event. Don't soft-transition.

### Pre-public-launch gate

Specified in detail in `build-spec.md` § Phase 6. At minimum:

- [ ] Threat model reviewed for every public surface
- [ ] Accessibility audit passed (WCAG AA target)
- [ ] Performance audit passed (LCP / TTI / Wizard p95 latency budgets met)
- [ ] SLOs defined and measured
- [ ] Alerts proven (synthetic failure → page lands somewhere)
- [ ] Runbooks exist and have been walked through at least once
- [ ] DR drill: cosmos restore from backup, AI Search index rebuild, deploy from clean
- [ ] Cost projections validated against actual burn for ≥ 30 days
- [ ] Content moderation policy + auth-gating reviewed for any user-input surface
- [ ] Live-demo URL stable, certs valid, Cloudflare WAF + Bot Fight active
- [ ] README + vision doc reflect what's actually live (no aspirational language for shipped features)

## Risk register

Living list. Format: `ID | description | severity | likelihood | mitigation | last reviewed`. Reviewed at every phase boundary.

| ID | Description | Severity | Likelihood | Mitigation | Last reviewed |
| --- | --- | --- | --- | --- | --- |
| R1 | Showcase narrative undersold while AI tracks (C/D/E) unstarted | High | **Resolved 2026-05-13** | README rewrite (PR #209) + live system deployed through Phase 6; prospect-visible demonstrable artifact exists end-to-end. | 2026-05-13 |
| R2 | Stale Playwright 1.12.0 dependency carries records workaround | Medium | **Resolved 2026-05-11** | Playwright upgraded to 1.59.0 (Phase 5 Wave 1). Records workaround documented in DL-0002; `SternPlaywrightDtoActivatorContractTests` pins the contract. | 2026-05-13 |
| R3 | Open Dependabot PRs against deprecated path send "unmaintained" signal | Low | **Resolved 2026-05-13** | All Dependabot PRs triaged: deprecated-path PRs (#199, #200) closed; clean PRs (#197, #198, #203) merged. | 2026-05-13 |
| R4 | Stern Playwright scrapers lack scraper-pipeline integration tests | Low | Resolved (route ii) | Documented asymmetry in [`tests/PinballWizard.Scraper.Tests/README.md`](../tests/PinballWizard.Scraper.Tests/README.md) § "Stern Playwright asymmetry"; pinned by `SternPlaywrightAsymmetryDocumentationTests`. Revisit when a Playwright-route test fixture lands. | 2026-05-04 |
| R5 | AI Search + OpenAI cost overrun if usage scales unexpectedly | High | **Migrated to monitored-in-steady-state 2026-05-13** | Cost alerts active ($300/day threshold) + App Insights cost tile in "PinballWizard Ops" workbook. 30-day burn snapshot pending (see Phase 6 § Retrospective). Monthly review cadence per this doc. | 2026-05-13 |
| R6 | Indefinite schedule drift without urgency forcing function | Medium | **Closed 2026-05-13** | All 6 phases shipped. Phase gates + per-phase exit checklists held the cadence across a 10-day accelerated build. | 2026-05-13 |
| R7 | Quality-gate erosion (deferred ⚠️ becomes routine) | Medium | Possible | Monthly review of `/local-review` outcomes; ratchet rule: never lower a gate | 2026-05-04 |
| R8 | Locked-decision soft-erosion via small concessions | High | Possible | This doc § "Locked decisions"; explicit relitigation requirement | 2026-05-04 |
| R9 | Source site changes (DOM, robots.txt, ToS) break a scraper or revoke permission | Medium | Likely over time | Per-source health checks; politeness-overrides in Cosmos; monthly source review | 2026-05-04 |
| R10 | Personal-account constraint accidentally violated (work email, work tenant) | High | Low | Identity check in 7-item audit; sanitization workflow; this doc goal 6 | 2026-05-04 |
| R11 | Bicep alert/RBAC/diagnostics bugs not caught by `what-if` validation | Medium | Likely on first deploy of new resource types | Full apply against dev before merging any PR that adds alert rules, KEDA triggers, diagnostic settings, or role assignments. Budget for fix-up PRs. (Phase 6 lesson — 7 bugs / 3 fix PRs.) | 2026-05-13 |
| R12 | `pinwiz.ai` live surface not yet serving real app (placeholder ACA image) | High | Certain (current state) | Containerize and deploy Blazor app as first Phase 7 action. Scopes 11+12 (Lighthouse / axe-core live) are blocked until resolved. | 2026-05-13 |
| R13 | Per-IP rate limiting not implemented in code (Cloudflare Bot Fight is sole defence) | Medium | Possible post-launch | Monitor Cloudflare logs for abuse patterns; add ASP.NET Core rate-limiting middleware if patterns emerge. Deferred from Phase 6 per threat-model.md. | 2026-05-13 |

New risks land here, not in memory. Memory snapshots state at a moment; the risk register is the canonical living list.

## Spec maintenance

Each spec doc has a clear update trigger. If a change happens that matches a trigger, the doc gets updated *in the same PR* as the change — never as follow-up.

| Doc | Update triggers |
| --- | --- |
| `vision.md` | Goal change (rare); brand or domain change; explicit scope shift to/from "showcase" |
| `build-spec.md` | Phase boundary; scope change within a phase; new feature accepted into a bucket; deferral; phase-gate completion |
| `quality-spec.md` | New quality gate added; existing gate modified; threshold change; tool migration (e.g., adopt Stryker) |
| `guardrails.md` | New anti-pattern surfaced; new escalation trigger; risk register update; locked decision added or removed |
| ADRs (`docs/adr/*`) | Any non-obvious technical decision — append-only; supersede via new ADR, never edit history |
| `decision-log.md` | Any sub-ADR-threshold decision worth retrieving later |
| `CLAUDE.md` | Any locked invariant change; any change in tooling, CLI flags, or showcase obligations |
| `README.md` | Any change visible to a GitHub-landing prospect; phase milestone; live-demo URL change |
| Memory (`MEMORY.md` + entries) | Anything durable that future sessions need; never as a substitute for spec docs |

**Spec docs vs. memory:** spec docs are the canonical project artifact for prospects and humans; memory is Claude's session-spanning context. When information belongs in both, the spec doc wins; memory entries reference the spec, not the other way around.

## Decision log

Lives at `docs/decision-log.md` (to be created when first entry lands). Distinct from ADRs:

- **ADRs** capture architectural decisions with significant trade-offs, alternatives evaluated, and consequences. Heavyweight; permanent record.
- **Decision log** captures sub-ADR decisions: tool versions, library choices within a category, parameter values, naming conventions, threshold settings.

Format per entry:

```text
## YYYY-MM-DD — [Short title]
**Decision:** ...
**Alternatives considered:** ...
**Rationale:** ...
**Revisit when:** ...
**Related:** PR #XX, ADR-YYYY (if any)
```

Append-only. Decisions reverse via a new entry that supersedes the prior one.

## Escalation triggers

Conditions that **stop work in flight**, not "after the current PR." Each has a clear stop-and-do-X protocol.

### Build-time

| Trigger | Action |
| --- | --- |
| Security finding mid-PR (CodeQL alert, secret in commit, dep CVE high/critical) | Stop. Triage severity. Fix or roll back commit. Don't push until clean. |
| Identity check fail (`git log -1 --format='%an <%ae>'` shows work email) | Stop. Reset HEAD. Re-author with personal noreply. Verify before continuing. |
| Locked-decision conflict | Stop. Surface the conflict and the proposed deviation. Wait for explicit user decision. |
| Polite-scraping invariant violation (raw `HttpClient.GetAsync` in scraper, robots.txt skipped, no User-Agent) | Stop. Fix at source. Don't merge with the violation present. |
| Provenance loss in a data path | Stop. Reinstate the chain. This is a 🔴 always. |
| Scope explosion (PR adds files / responsibilities outside its stated purpose) | Stop. Split. Each PR has one purpose. |
| Test regression that was previously green | Stop. Don't disable the test. Don't add `[Skip]`. Root-cause it. |
| Build warnings emit | Stop. `TreatWarningsAsErrors` is the bar; treat warnings as errors regardless. |

### Run-time (Phase 6+, when the live system exists)

| Trigger | Action |
| --- | --- |
| SLO breach sustained > 15 min | Page (Phase 6 spec). Investigate before user impact compounds. |
| Cost burn-rate alarm ($300/mo threshold or daily anomaly) | Investigate within 24 hours. Identify cause. Adjust threshold or fix root cause. |
| Source site error rate spike (any single source > 5% errors over a run) | Throttle further or pause source. Review robots.txt and ToS. |
| Robots.txt change to `Disallow: /` on a previously-permitted source | Stop scraping that source. Update `IngestionSource.enabled = false` immediately. Reconfirm with polite outreach before resuming. |
| Wizard p95 latency > 2× target sustained | Investigate retrieval latency, RU consumption, embedding cache hit rate. |
| Citation-accuracy eval-set regression > 5% | Stop deploying retrieval/chunking changes. Root-cause against eval set. |

## Self-evaluation cadence

Drift is detected by re-asking the same questions on a schedule. The cadences:

- **Per PR:** `/local-review` + 7-item self-audit (existing).
- **Per phase boundary:** full per-phase gate checklist; risk register review; spec doc freshness check.
- **Monthly (calendar-driven, even with no active work):** re-read this doc + `vision.md`. Ask: are we still aligned with the seven goals? Has the showcase narrative drifted? Are deferred items still rightly deferred? Is the risk register current?
- **Pre-public-launch (Phase 6):** full pre-public-launch gate checklist.
- **Post-public-launch (steady state):** monthly review of cost, SLOs, source-site health, citation-accuracy eval set; monthly review of this doc continues.

The monthly cadence is the most easily-skipped and the most valuable. Block calendar time for it; treat the recurring slot as load-bearing, not optional.

An eval metric dismissed as a "measurement artifact" in two consecutive committed eval runs must, in the next PR touching the harness, either get its measurement fixed or be removed — a metric the team has learned to ignore is anti-observability.

## When this document is wrong

If a guardrail in this document obstructs delivery of the seven main goals — *not* obstructs convenience, but actually obstructs the goals — the document is wrong, not the goals. In that case:

1. Stop work on the immediate task.
2. Surface the conflict to the user with a one-paragraph case for amending this doc.
3. Wait for explicit confirmation.
4. Update this doc *and* the spec doc the change affects in the same PR.
5. Resume.

Do not work around a guardrail silently. Guardrails that are routinely bypassed are no guardrails at all.
