# Quality spec

The comprehensive HOW of quality for PinballWizard / pinwiz.ai. Catalogues every quality gate — currently enforced and to-be-added — across code, tests, review, documentation, operations, accessibility, security, and cost. This is the doc that makes "enterprise quality bar" mechanically checkable rather than aspirational.

Read alongside [`vision.md`](vision.md) (why), [`guardrails.md`](guardrails.md) (rules and decisions), and [`build-spec.md`](build-spec.md) (the phased plan). The relationship: vision sets the goals, guardrails sets how scope is defended, build-spec sets what ships in what order, this doc sets the gates each shipped artifact must clear.

Forward-looking by design: gates that don't yet exist are listed with their target phase placement so a prospect can see the full quality posture even before every gate is implemented. Per [`guardrails.md`](guardrails.md) risk R7, the **ratchet rule** applies: never lower a gate. Coverage thresholds, eval-set scores, performance budgets only move up.

## Definition of Done

### Per PR

The canonical per-PR gate is the two-step pre-push audit. Full detail in [`CLAUDE.md`](../CLAUDE.md) § "PR self-audit" and [`guardrails.md`](guardrails.md) § "Per-PR gate" — not duplicated here. Recap:

- `/local-review` skill (10-category qualitative critique by `general-purpose` agent)
- 7-item mechanical self-audit (dead-config grep, sibling-diff, no bare `catch`, CLI/orchestrator wiring, behavior-not-structure tests, zero warnings, identity check)
- Build green, tests green
- PR description records the audit outcome
- Memory updated if anything new is locked

This doc adds nothing to the per-PR gate beyond what `guardrails.md` already specifies. The per-PR gate is intentionally tight; deeper gates fire at phase and release boundaries.

### Per phase

Reference: [`guardrails.md`](guardrails.md) § "Per-phase gate" — 9-item structural checklist. Quality-specific additions (these are the items this doc owns):

- **Code quality:** all currently-in-place gates green; coverage threshold met for new code (Phase 2+); architecture fitness tests pass after phase additions (Phase 3+); mutation test run on phase's new code shows no test-quality regression (Phase 3+).
- **Test quality:** behavior-not-structure rule applied (`/local-review` § Test quality enforces); contract tests still pass; phase-relevant evaluation harness regressions are zero (Phase 3+).
- **Documentation:** all spec docs current per § "How this doc evolves" below; ADRs committed for any non-obvious decision the phase generated; decision-log entries for sub-ADR decisions.
- **Risk register:** every risk in `guardrails.md` reviewed against phase outcomes; resolved risks marked, new risks added, mitigations updated.

### Per release (pre-public-launch)

Reference: [`guardrails.md`](guardrails.md) § "Pre-public-launch gate" — 11-item structural checklist (threat model, accessibility, performance, SLOs, alerts, runbooks, DR drill, cost validation, etc.). Quality-specific additions:

- **All quality gates above, applied to the entire codebase**, not just the phase that introduced them — coverage / mutation / architecture fitness / contract tests all green over the full repo.
- **Accessibility audit pass:** axe-core via Playwright on every public page; WCAG AA target met; manual screen-reader smoke test (NVDA) pre-launch.
- **Performance audit pass:** Lighthouse CI thresholds met; Wizard p95 latency budget verified under realistic load.
- **Security audit pass:** threat model per public surface reviewed and dated; CVE-response SLA validated against current Dependabot state; secret rotation cadence documented.
- **Eval-set baseline frozen:** retrieval / citation-accuracy / answer-quality eval scores recorded as the "v1 launch baseline." Future regressions are measured against this baseline.
- **Cost projection vs. actual:** ≥ 30 days of actual burn data validates the projection; budget alarm tested with synthetic spike.

## Code quality

### Currently in place

- [`Directory.Build.props`](../Directory.Build.props): `TreatWarningsAsErrors = true`, `AnalysisLevel = latest-recommended`, nullable enabled
- [`Directory.Packages.props`](../Directory.Packages.props): central package management; per-project `<PackageReference>` entries don't carry versions
- [`global.json`](../global.json): SDK pinning so dev / CI builds are reproducible
- Locked-mode NuGet restore in CI: `dotnet restore --locked-mode`; `packages.lock.json` committed
- CodeQL static analysis: [`.github/workflows/codeql.yml`](../.github/workflows/codeql.yml)
- Sanitization workflow: [`.github/workflows/sanitization.yml`](../.github/workflows/sanitization.yml) — blocks personal-email leakage and work-email leakage into committed files (both patterns enforced; see Security quality § Currently in place for detail)
- Bicep syntax validation: [`.github/workflows/bicep.yml`](../.github/workflows/bicep.yml)
- Dependabot: [`.github/dependabot.yml`](../.github/dependabot.yml) — weekly bumps
- `.slnx` solution format
- **Test coverage threshold + CI enforcement**: CI runs dotnet test with coverage.runsettings and fails if aggregate production line coverage drops below **70%** (current: 74%; ratchet up at phase boundaries per the ratchet rule). See [decision-log.md](decision-log.md) entry 2026-05-30 for the per-assembly policy.

  **Per-assembly policy** (cultural gate — verified during PR review via the coverage table; irongut/CodeCoverageSummary enforces only the aggregate):

  | Assembly | Floor | Rationale |
  | --- | --- | --- |
  | PinballWizard.Core | ≥ 80% | Pure domain logic; high testability |
  | PinballWizard.Application | ≥ 80% | Orchestration + AI routing; high testability |
  | PinballWizard.Api | ≥ 75% | Endpoint middleware + problem details |
  | PinballWizard.ServiceDefaults | ≥ 75% | Resilience config + health check endpoints |
  | PinballWizard.Infrastructure | ≥ 65% | Playwright scrapers and Cosmos/Azure SDK I/O paths are architecturally untestable in unit tests |
  | PinballWizard.Web | ≥ 65% | Razor render-tree; bUnit smoke tests + axe-core are the load-bearing quality gates, not line rate |
  | PinballWizard.Cli | excluded | DI composition only |
  | PinballWizard.RagIngestionWorker | excluded | DI composition only |

  Phase 5 baselines: Core 75%, Application 90%, Api 84%, ServiceDefaults 84%, Infrastructure 66%, Web 65%.
- **Accessibility tests (axe-core via Playwright)**: [`tests/PinballWizard.Web.Tests/A11y/`](../tests/PinballWizard.Web.Tests/A11y/) — `PlaywrightWebApplicationFactory` starts the app on a real Kestrel port; `AccessibilityTests` navigates a headless Chromium browser to every public route (`/`, `/wizard`, `/settings`, `/error`, `/tilt`) and runs axe-core with `wcag2a` + `wcag2aa` tags. Zero violations required; failing axe rule = failing CI job. Runs as a separate parallel job in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) `accessibility` job.

### To add

| Gate | Phase | Notes |
| --- | --- | --- |
| Mutation testing (Stryker.NET) | 3 | Validates the "tests assert behavior" rule mechanically. Cadence: per-phase (full run) + nightly on `main` (incremental). Threshold: ≥ 70% mutation score on `Core` + `Application`; ≥ 50% on `Infrastructure` (which has more I/O integration code). Initial baseline taken at Phase 3 entry. |
| Architecture fitness tests (NetArchTest) | 3 | Mechanical layering enforcement. Initial assertions: `Application` doesn't reference `Microsoft.Azure.Cosmos` or `Azure.ResourceManager.*`; `Core` has zero external package references; `Infrastructure.Scraping.<Mfg>` doesn't reference other manufacturer namespaces; no `public` types in `Internal/` folders. Failures fail the build. |

## Test quality

### Currently in place

- **bUnit component tests (Phase 5)**: [`tests/PinballWizard.Web.Tests/Components/`](../tests/PinballWizard.Web.Tests/Components/) — component-level unit tests for every interactive Razor component using bUnit 2.x. 29 test files covering the delight surfaces (`WizardAnswerStream`, `RefusalPanel`, `CitationStrip` family, `TiltPage`/`TiltErrorBoundary`) and admin components. Required for any component beyond static markup; any new interactive component must add a bUnit smoke test in the same PR per PR self-audit item 9d.
- **Lighthouse CI (Phase 5)**: [`.github/workflows/lighthouse.yml`](../.github/workflows/lighthouse.yml) — runs on every PR touching Blazor/Web files. Thresholds enforced via `.lighthouserc.json`: Performance ≥70 (warn), Accessibility ≥90 (error), Best-Practices ≥90 (error), SEO ≥90 (warn). Failing threshold = failing CI job. Complements the axe-core accessibility gate in `Code quality § Currently in place`.
- **End-to-end (E2E) suite**: [`tests/PinballWizard.Web.Tests/E2E/WizardE2ETests.cs`](../tests/PinballWizard.Web.Tests/E2E/WizardE2ETests.cs) — four browser-driven (Playwright) tests against the live deployed app, run by the 6-hourly scheduled canary + the post-deploy gate; CI-excluded (they need a live stack). Full per-test inventory in [`ENGINEERING_STANDARDS.md` § 7.1 — What we test](ENGINEERING_STANDARDS.md#71-what-we-test).

### Discipline (currently cultural; mutation testing will validate mechanically from Phase 3)

- **Tests assert behavior, not structure.** A test named "deduplicates" must include a fixture where dedup actually fires; "rejects merch" must include merch in the input. `/local-review` § Test quality is the enforcer.
- **Test naming:** `Method_State_Expectation` (e.g., `ExtractSlug_NullArg_Throws`, `Sync_FetchedItemAlreadyPresent_SkipsWrite`).
- **Test project naming:** `<ProductionProject>.Tests` for unit tests, `<ProductionProject>.IntegrationTests` for tests requiring live dependencies, `<ProductionProject>.E2ETests` for browser-driven tests. One project per production project. See [ADR-0030](adr/0030-test-project-naming.md) for the full convention and the project mapping table.
- **Integration test infrastructure:** `FakePolitenessGate` + `QueueingHttpMessageHandler` in `tests/PinballWizard.Infrastructure.Tests/Scraping/Pipelines/`; pins yield order, full provenance, gate-vs-wire URL equality, per-page failure isolation, and `PolitenessException` propagation on both Acquire and Report paths.
- **Contract tests pin invariants:** `SourceAliasContractTests` ensures every `ISourceScraper.Name` matches its `--source` alias; adding a scraper without that test passing is a 🔴.
- **Behavior over coverage padding.** Coverage measures lines exercised; mutation testing measures whether tests would catch a bug. The latter is the load-bearing metric from Phase 3 onward.

### To add

| Gate | Phase | Notes |
| --- | --- | --- |
| Retrieval / answer-quality evaluation harness | 3 | Held-out set of pinball questions with known correct citations + expected answer themes. Scored continuously; results trended in `eval/` directory. Routing-decision tests (gpt-4o vs gpt-4.1) validated. Threshold-driven refusal validated. |
| Citation-accuracy eval set | 4 | Specifically: % of Wizard answers that include a clickable, valid citation pointing at a real source URL in the catalog. v1 target: ≥ 95%. Lower threshold = the "I don't know" path needs strengthening, not the citation pipeline. |
| Load / latency regression tests (k6) | 5+ | k6 or similar for Wizard p95 latency under realistic query load. Complements Lighthouse CI (already in place). Target: Wizard p95 < 2s end-to-end. |

## Review process

### Per-PR (existing; recap from `guardrails.md`)

`/local-review` (10-category qualitative) + 7-item mechanical self-audit + PR template.

### Heavyweight triggers

| Trigger | Tool | When |
| --- | --- | --- |
| Auth, identity, secrets, user input, external API surface | `/security-review` | Any PR touching the surface |
| Cross-cutting refactor (≥ 3 layers, ≥ 5 files outside one feature directory) | `/ultrareview` | User-triggered; recommend explicitly when criteria met |
| Phase boundary | `/local-review` against cumulative diff since last phase | Every phase exit; catches drift the per-PR review missed |
| Pre-public-launch | Operational readiness review | Phase 6 specification — full pre-launch gate checklist |

The per-PR gate handles ~95% of PRs. Heavyweight reviews are reserved for the cases where blast radius or surface area justifies the cost.

## Documentation quality

### Spec doc currency

Every spec doc has an explicit update trigger; updates land *in the same PR* as the work they describe, never as follow-up. Full table in [`guardrails.md`](guardrails.md) § "Spec maintenance". Recap:

- [`vision.md`](vision.md) — only on goal change (rare)
- [`build-spec.md`](build-spec.md) — every phase boundary, scope change, deferral, completion
- [`quality-spec.md`](quality-spec.md) (this doc) — when a gate is added / modified / threshold-changed
- [`guardrails.md`](guardrails.md) — anti-pattern surfaced, escalation trigger added, risk register update
- [`CLAUDE.md`](../CLAUDE.md) — locked invariant change, tooling change
- [`README.md`](../README.md) — phase milestone visible to prospects, live-demo URL change
- [`docs/adr/`](adr/) — append-only per non-obvious decision
- `docs/decision-log.md` — sub-ADR decisions

### ADR triggers

A decision warrants an ADR when **all four** are true:

1. The decision has significant trade-offs.
2. Alternatives were genuinely considered (not just default-accepted).
3. The consequences extend beyond the immediate PR.
4. Future readers (including future-Claude) would benefit from the permanent record.

Decisions that are smaller — tool versions within a category, threshold settings, naming conventions — go in `docs/decision-log.md` instead.

### Explicit non-gate

**XML doc completeness on public surface is not a quality gate.** Per [`feedback_no_xml_docs.md`](C:\Users\JimKeeley\.claude\projects\c--projects-PinballWizard\memory\feedback_no_xml_docs.md): user finds them not worth the maintenance cost; project doesn't ship a public NuGet package; `/local-review` § Comments policy explicitly does not flag missing XML docs. Revisit only if a NuGet package ships externally.

## Operational quality (Phase 6)

### SLOs / SLIs (Phase 6 — shipped)

SLI targets are defined in [`build-spec.md`](build-spec.md) § Phase 6 and enforced via the alert rules below. Six SLIs with v1 targets:

| SLI | v1 Target | Notes |
| --- | --- | --- |
| Wizard p95 query latency | < 2 seconds end-to-end | Easy with AI Search Basic + ACA min=1; hard with min=0 cold starts (acceptable during build, not in live) |
| Wizard first-token latency p95 | < 3 seconds | SSE streaming first token; alert rule: `alertLatency` in [`infra/modules/shared.bicep`](../infra/modules/shared.bicep) |
| 5xx error rate | < 1% over 5-minute window | Alert rule: `alert5xx` in shared.bicep |
| Availability | 99.5% | Single-region; multi-region failover deferred to v2. Alert rule: `alertAvailability` in shared.bicep |
| Citation accuracy | ≥ 95% of Wizard answers include a clickable valid citation | Below threshold = strengthen the "I don't know" path, not the citation pipeline |
| Cost-per-query | Tracked + trended; budget alarm at $300/mo | Alert rule: `alertCost` in shared.bicep; dead-letter backlog surfaced via `alertDeadLetters` |

### Alerting (Phase 6 — shipped in Bicep, `deployPhase2`-gated)

- **Action group `pinwiz-ops-alerts`**: routes to `jim@earlybirdsolutions.com` via `opsActionGroup` in [`infra/modules/shared.bicep`](../infra/modules/shared.bicep).
- **5 alert rules** (log-search, `Microsoft.Insights/scheduledQueryRules@2023-03-15-preview`): `alertLatency`, `alert5xx`, `alertCost`, `alertDeadLetters`, `alertAvailability`. All emit to `opsActionGroup`.
- **Threshold-driven:** sustained breach > 15 min fires the rule.
- **Noise budget:** ≤ 1 page per week in steady state. Exceeded = the alert is wrong (too sensitive, false-positive prone) and gets retuned, not the system.
- **Dashboard**: Application Insights workbook "PinballWizard Ops" (`opsWorkbook` in shared.bicep) — 7 tiles covering all six SLIs. Workbook JSON embedded from [`infra/dashboards/pinwiz-ops-workbook.json`](../infra/dashboards/pinwiz-ops-workbook.json) at deploy time.

### Runbooks (Phase 6 — shipped)

Runbook inventory in [`docs/runbooks/README.md`](runbooks/README.md). Six runbooks shipped with `Last walked: Not yet walked` (pre-H-chain status; will be updated after first operational drill):

- [`01-incident-response.md`](runbooks/01-incident-response.md) — Wizard down, Cosmos down, AI Search down, OpenAI rate-limited, Cloudflare WAF mistuned
- [`02-cost-anomaly.md`](runbooks/02-cost-anomaly.md) — alarm fires → investigate, throttle, escalate if user impact, document
- [`03-cosmos-restore.md`](runbooks/03-cosmos-restore.md) — restore from backup; procedure to be walked pre-launch
- [`04-ai-search-rebuild.md`](runbooks/04-ai-search-rebuild.md) — index rebuild; procedure to be walked pre-launch
- [`05-secret-rotation.md`](runbooks/05-secret-rotation.md) — OPDB API token, Cloudflare API token, managed identity; 90-day cadence for human-managed secrets
- [`06-source-site-outage.md`](runbooks/06-source-site-outage.md) — single source returns errors → throttle further or pause via `IngestionSource.enabled = false` flip

### DR testing cadence

DR drills are defined; not yet executed (pre-launch gate requires first walk-through):

- Cosmos restore drill: pre-launch + every 6 months (`03-cosmos-restore.md`)
- AI Search index rebuild: pre-launch + every 6 months (`04-ai-search-rebuild.md`)
- Deploy from clean: pre-launch + every 12 months (H-DR-Cosmos, H-DR-Search hand-off chain)
- Secret rotation: continuous (rotate in place every 90 days for human-managed secrets; managed identity rotation handled by Azure)

## Accessibility / UX quality (Phase 5)

Phase 5 spec details these; gates listed here for the consolidated catalogue.

| Gate | Target | Verification |
| --- | --- | --- |
| WCAG AA conformance | All public pages | axe-core via Playwright in CI; failing rule = failing build |
| LCP (Largest Contentful Paint) | < 2.5s mobile + desktop | Lighthouse CI thresholds |
| TTI (Time to Interactive) | < 3.8s mobile + desktop | Lighthouse CI thresholds |
| CLS (Cumulative Layout Shift) | < 0.1 | Lighthouse CI thresholds |
| Wizard p95 latency (UI-perceived) | < 2s end-to-end | k6 / load test with realistic query mix |
| Mobile responsive | Major breakpoints (320, 768, 1024, 1440) | Playwright responsive snapshots on every Blazor PR |
| Keyboard navigation | All interactive elements reachable via Tab | Manual smoke test pre-launch + Playwright keyboard-nav assertions |
| Screen reader smoke | NVDA on every public page | Manual pre-launch; if any blocker, defer the feature |

## Security quality

### Currently in place

- CodeQL static analysis on every PR
- Sanitization workflow blocks personal **and** work email leakage (both patterns enforced via [`WORK_EMAIL_PATTERN`](../.github/workflows/sanitization.yml) repo secret — shipped Phase 2 § Scope item 9); failing CI on either. Closes the manual-trigger gap noted in `feedback_personal_identity_only.md`.
- Dependabot weekly bumps catch known CVEs in direct + transitive deps
- Personal-account isolation enforced by 7-item audit identity check (`git log -1 --format='%an <%ae>'`)
- Locked-mode NuGet restore prevents supply-chain drift between dev and CI
- ADR 0010 — Personal Azure subscription only; no work tooling integration
- **Auth flow review (Phase 5/6)**: blanket `FallbackPolicy` (`RequireAuthenticatedUser`) wired in [`src/PinballWizard.Web/Program.cs`](../src/PinballWizard.Web/Program.cs); public routes opt out with `[AllowAnonymous]`; admin routes protected by the policy without redundant `[Authorize]`. Authorization contract tests in [`tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs`](../tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs) pin every public page (must have `[AllowAnonymous]`) and every admin page (must not have `[AllowAnonymous]` or redundant `[Authorize]`). Failing contract test = failing CI.
- **Secret rotation cadence (Phase 6)**: rotation procedure documented in [`docs/runbooks/05-secret-rotation.md`](runbooks/05-secret-rotation.md). OPDB API token: 90 days. Cloudflare API token: 90 days. Managed identity client secrets: handled by Azure (continuous). All rotations logged in decision-log.
- **Threat model (Phase 6)**: [`docs/threat-model.md`](threat-model.md) — STRIDE-light analysis of 5 public surfaces (anonymous Wizard SSE endpoint, `/admin`, static pages, health endpoints, scraper outbound). 8 residual risks documented with severity and trigger condition. Reviewed 2026-05-11; revisit trigger: any PR that adds a new public route or changes auth on an existing one.

### To add

| Gate | Phase | Notes |
| --- | --- | --- |
| Dependency CVE response SLA | 5 | High/critical: patch within 48 hours. Medium: 7 days. Low: next monthly review. Tracked via Dependabot alerts. Policy document not yet written. |
| Content moderation policy | 7+ | OCR score capture, Strategy Tracker entries, Dream Game outputs. Auth-gated + abuse rate limit + denylist for high-risk inputs. Specific to each user-input surface. |

## Cost quality

### Currently in place

- $300 anomaly alarm — `alertCost` log-search alert rule in [`infra/modules/shared.bicep`](../infra/modules/shared.bicep), routes to `pinwiz-ops-alerts` action group; Bicep-managed, `deployPhase2`-gated
- $400 hard cap target (process-level; not enforced in code or infra)
- Two-tier Bicep deploy ([ADR-0013](adr/0013-two-tier-bicep-deploy.md)) — implements the cost ceiling at the infrastructure level; Phase 1 default, Phase 2 resources gated on `deployPhase2 = true`
- Cosmos serverless billing (consumption-based, not provisioned throughput) — keeps idle cost near zero
- **Anomaly response runbook (Phase 6)**: [`docs/runbooks/02-cost-anomaly.md`](runbooks/02-cost-anomaly.md) — when the alarm fires: investigate cause, throttle the responsible feature, escalate if persistent, document. Shipped Phase 6 Wave 1.

### To add

| Gate | Phase | Notes |
| --- | --- | --- |
| Per-feature cost attribution | 4+ | OTel tags + Log Analytics queries breaking down cost by feature (RAG vs scraper vs admin telemetry). Needed once Phase 2 Bicep flips and AI Search / OpenAI start billing meaningfully. |
| Monthly cost review | 6 | Calendar event paired with the monthly self-evaluation cadence (per `guardrails.md` § Self-evaluation cadence). Review burn vs. projection; if drifting toward $300 alarm, identify cause and adjust. |
| Cost burn-rate dashboard | 6 | Real-time per-feature visibility surfaced in `/admin/cost`. Renders the per-feature attribution from above. |

## How this doc evolves

Per [`guardrails.md`](guardrails.md) § "Spec maintenance":

- **A new gate gets added:** this doc grows by one row in the relevant section, in the same PR as the gate's implementation. Add to the "To add" table during planning; promote to "Currently in place" when the gate ships and is enforced.
- **A gate gets modified** (threshold change, tool migration, scope change): update the row in place; if the change is non-trivial, add a decision-log entry pointing at the rationale.
- **A gate gets removed:** requires explicit user confirmation per `guardrails.md` § "Locked decisions" (some gates are effectively locked once they're load-bearing). Removed gates move to a `## Removed gates` section at the bottom with reason + date.
- **At every phase boundary:** cross-check that the gates promised for that phase actually shipped. If any didn't ship, either roll forward (with explicit acknowledgement) or update the phase placement.
- **At every monthly self-evaluation** (per guardrails): re-read this doc; check that the "currently in place" gates still reflect reality (CI workflows can decay silently); identify any "to add" gates whose phase placement should shift.

The ratchet rule is the load-bearing principle of this doc: **never lower a gate.** Coverage thresholds, eval-set scores, mutation scores, performance budgets only move up. If a regression forces a temporary lowering, document the regression as a risk in `guardrails.md` and schedule remediation before the next phase exit. Permanent gate-lowering requires explicit user confirmation and a decision-log entry.
