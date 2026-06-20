# PinballWizard Standards System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an enforcement-first, machine-checkable standards system under `.claude/standards/` that keeps long-running autonomous Claude sessions on the PinballWizard quality bar — verify-before-done, single source of truth.

**Architecture:** Six domain standards (RULE blocks: `WHEN/THEN/NEVER/CHECK/SEV/REF`) governed by a shared protocol contract, a mechanical `/standards-audit` gate alongside the qualitative `/local-review`, an xUnit anti-drift test, and a standards-canonical migration of `INVARIANTS.md` / `PR-AUDIT.md` / `local-review`. Adapts the APS `aps-*-standard` authoring discipline; drops all fleet-measurement machinery.

**Tech Stack:** Markdown (standards, skills, contracts), C# / xUnit (anti-drift test — `PinballWizard.Core.Tests`), `ripgrep` + `git` + `dotnet` (CHECK commands). Spec: [`docs/superpowers/specs/2026-06-20-pinball-standards-system-design.md`](../specs/2026-06-20-pinball-standards-system-design.md).

## Global Constraints

- **Branch:** all work on `feat/pinball-standards-system` (already created off clean `main`). Never commit on `main`.
- **Identity:** every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. No Claude attribution trailer (matches repo history).
- **Commit format:** conventional — `type(scope) message` (e.g. `docs(standards) …`, `feat(standards) …`, `test(standards) …`). Personal GitHub repo; no work-item references.
- **Rule IDs are append-only.** Never renumber or reuse an ID. A superseded rule is marked `Superseded by <ID> (<date>)`, never deleted.
- **No new policy.** Every RULE block ports an existing decision from `INVARIANTS.md`, `PR-AUDIT.md`, or the `/local-review` prompt. If a rule has no existing source, it is out of scope for this plan.
- **Posture is verify-before-done**, not APS "inform-never-block." Do not port the compliance banner, fleet registry, scoring %, archetype/traits matrix, scaffold/rollup tooling, or Draft→Active→Team lifecycle.
- **Standards canonical.** Converted domains' policy lives in `.claude/standards/`; `INVARIANTS.md` links into them. Wave-2 invariants keep a prose stub marked `→ standard pending`.
- **Rule-block parse contract (load-bearing — the anti-drift test depends on it):**
  - A rule begins with a line matching `^\*\*RULE ([A-Z]+-\d{2})\*\* \(([a-z0-9-]+)\)$`.
  - Every rule ID appears as a row in its domain's `REQUIREMENTS.md`.
  - Every `INVARIANTS.md` numbered entry either contains a `RULE <PREFIX>-NN` reference or the literal marker `→ standard pending`.

---

## File Structure

**Created:**
- `.claude/standards/README.md` — domain index (status, rule count, applies-to globs).
- `.claude/standards/pinball-standards-protocol.md` — shared enforcement contract + cross-cutting task-type DoD table.
- `.claude/standards/provenance/STANDARD.md` + `REQUIREMENTS.md`
- `.claude/standards/polite-scraping/STANDARD.md` + `REQUIREMENTS.md`
- `.claude/standards/persistence-cosmos/STANDARD.md` + `REQUIREMENTS.md`
- `.claude/standards/observability-and-honest-failure/STANDARD.md` + `REQUIREMENTS.md`
- `.claude/standards/testing/STANDARD.md` + `REQUIREMENTS.md`
- `.claude/standards/delivery/STANDARD.md` + `REQUIREMENTS.md`
- `.claude/skills/standards-audit/SKILL.md` — mechanical gate.
- `tests/PinballWizard.Core.Tests/Domain/StandardsConformanceTests.cs` — anti-drift test.

**Modified:**
- `.claude/INVARIANTS.md` — becomes an index linking into standards / pending markers.
- `.claude/PR-AUDIT.md` — shrinks to the two-skill invocation.
- `.claude/skills/local-review/SKILL.md` — prompt references rule IDs.
- `CLAUDE.md` — "Locked invariants" + "PR self-audit" sections point at the standards system.
- `.claude/README.md` — config-layer table gains the `standards/` row.

**Each `STANDARD.md`** carries frontmatter (`name`, `id-prefix`, `applies-to:` globs, `status`), the RULE blocks, and a `## Definition of Done` section.

---

## Task 1: Standards directory, protocol contract, and index

**Files:**
- Create: `.claude/standards/pinball-standards-protocol.md`
- Create: `.claude/standards/README.md`

**Interfaces:**
- Produces: the rule-block parse contract, the `applies-to:` glob convention, the 🔴/⚠️ severity taxonomy, the cross-cutting task-type DoD table, and the session lifecycle — all consumed by Tasks 2–13.

- [ ] **Step 1: Write the protocol contract**

Create `.claude/standards/pinball-standards-protocol.md`:

```markdown
# PinballWizard Standards Protocol

Shared enforcement contract for every PinballWizard standard under
`.claude/standards/`. Loaded at session start (referenced from `CLAUDE.md`)
and by the audit skills. This is the deliberate inverse of APS
`aps-standards-protocol`: APS standards *inform, never block*; PinballWizard
standards **verify before done**.

## Posture

PinballWizard is one app, one owner, shown to prospective clients. The agent
is the enforcer. A rule is not advice — it is a precondition for "done."
Enforcement is via this protocol + machine-checkable CHECK commands +
per-task Definition of Done. It does NOT rely on git hooks (the repo's
`track-gates` hook mis-fires — see `memory/reference_workflow_gates_not_firing.md`).

## Severity taxonomy

- **🔴 blocking** — must be fixed before the commit/push that introduces the
  change. A 🔴 fail in `/standards-audit` refuses to proceed.
- **⚠️ advisory** — fix, or defer with a one-line justification recorded in
  the PR description.

There are no deferred 🔴s. "I'll fix it in a follow-up" does not apply to 🔴.

## Applicability resolution

1. Compute the changed-file set: `git diff --name-only origin/main...HEAD`
   plus `git diff --name-only` (uncommitted) plus untracked from
   `git status --short`.
2. For each standard under `.claude/standards/*/STANDARD.md`, read its
   frontmatter `applies-to:` glob list.
3. A standard is *applicable* if any changed file matches any of its globs.
4. Run the rules of every applicable standard.
5. If no changed file matches any standard, the audit reports
   **"clean — no governed surface touched"** (an explicit clean result, never
   a silent pass).

## No-relitigation

Every rule carries a `REF` to a settled ADR / invariant / incident. Rules
encode locked decisions. If the agent believes a rule is wrong, it surfaces
that to the owner — it does not silently deviate. Relitigating a locked
decision mid-session is itself a drift failure.

## Anti-rationalization

| Excuse | Reality |
|---|---|
| "The change is small — I'll skip the audit" | Small changes regress invariants too. Run `/standards-audit`. |
| "I'll fix the provenance gap in a follow-up" | 🔴 rules block *this* commit. No deferred 🔴. |
| "Tests are green, so I'm done" | Green tests ≠ DoD met. Run the task-type DoD below. |
| "I'm mid-session, the rules are already in context" | After any context summarization, re-load `README.md` before claiming compliance. |
| "No standard obviously applies" | Resolve applicability by glob, do not eyeball it. |
| "The owner said don't touch anything else" | That governs what you EDIT, not whether you RUN the audit. Run it. |

## Red flags — STOP and re-read this protocol

- About to push without running `/standards-audit`.
- About to mark a work unit done without running its task-type DoD.
- About to deviate from a 🔴 rule.
- About to relitigate a decision a rule's `REF` already settled.

## Definition of Done — by task type

Each row is the closing checklist for that kind of change. Run it before
marking the work unit done. (Domain rule sets are defined in each
`STANDARD.md`.)

| Task type | Composed DoD |
|---|---|
| **new scraper** | PROV-01..03 · POLITE-01..04 · TEST-02 (SourceAlias contract test passes) · DLV-03 (zero-warning build) |
| **new Cosmos read/write** | COSMOS-01..04 · TEST (CrossPartitionQueryAllowListTests passes) · OBS-04 (RU/duration metered) |
| **new degraded/fallback path** | OBS-01 (visible) · OBS-04 (log+meter) · TEST-01 (fixture proves the failure is observable) |
| **infra script change** | DLV-02 (Deployment Stacks only) · DLV-05 (no hardcoded sub IDs) |
| **any production-code change** | DLV-01 (identity) · DLV-03 (zero-warning) · DLV-04 (conventional commit) · the applicable-by-glob domains above |

## Session lifecycle

- **Start:** load `.claude/standards/README.md` + this contract.
- **Per work unit:** run the touched domains' Definition of Done.
- **Pre-commit:** `/standards-audit` on the staged diff.
- **Pre-push / PR:** `/local-review` (qualitative) + `/standards-audit` (mechanical) = the full gate.
- **After context summarization:** re-load `README.md` to re-anchor the rule namespace.

## Rule-block format (authoring contract)

    **RULE <PREFIX>-NN** (slug)
    WHEN:   <trigger condition>
    THEN:   <required action / state>
    NEVER:  <prohibited antipattern>
    CHECK:  <grep/glob/test command, OR "(qualitative — /local-review)">
    SEV:    🔴 | ⚠️
    REF:    <INVARIANTS#N · ADR-XXXX · incident-date>

IDs are append-only; never renumbered or reused. A superseded rule keeps its
ID and is marked `Superseded by <ID> (<date>)`.
```

- [ ] **Step 2: Write the index**

Create `.claude/standards/README.md`:

```markdown
# PinballWizard Standards

Machine-checkable, enforcement-first standards for autonomous-session
control. Posture and shared rules: [`pinball-standards-protocol.md`](pinball-standards-protocol.md).

| Domain | Prefix | Status | applies-to (summary) |
|---|---|---|---|
| [provenance](provenance/STANDARD.md) | `PROV-` | active | scrapers, catalog, RAG chunk mappers |
| [polite-scraping](polite-scraping/STANDARD.md) | `POLITE-` | active | `src/**/Scraping/**` |
| [persistence-cosmos](persistence-cosmos/STANDARD.md) | `COSMOS-` | active | `src/**/Persistence/**`, Cosmos options/repos |
| [observability-and-honest-failure](observability-and-honest-failure/STANDARD.md) | `OBS-` | active | fallback paths, health, logging, metrics |
| [testing](testing/STANDARD.md) | `TEST-` | active | `tests/**`, contract tests |
| [delivery](delivery/STANDARD.md) | `DLV-` | active | commits, `infra/scripts/**`, runbooks, build |

**Wave 2 (standard pending):** rag-agent, frontend-blazor, community-posture, iac-deploy — tracked as prose stubs in [`../INVARIANTS.md`](../INVARIANTS.md).

Run [`/standards-audit`](../skills/standards-audit/SKILL.md) (mechanical) and `/local-review` (qualitative) before any push.
```

- [ ] **Step 3: Verify the files render and the contract is internally consistent**

Run: `rg -n "RULE <PREFIX>-NN|applies-to|verify before done" .claude/standards/`
Expected: matches in both files; no literal `<placeholder>` left except inside the fenced format example.

- [ ] **Step 4: Commit**

```bash
git add .claude/standards/pinball-standards-protocol.md .claude/standards/README.md
git commit -m "feat(standards) protocol contract + domain index"
```

---

## Task 2: provenance standard

**Files:**
- Create: `.claude/standards/provenance/STANDARD.md`
- Create: `.claude/standards/provenance/REQUIREMENTS.md`

**Interfaces:**
- Consumes: rule-block format + parse contract from Task 1.
- Produces: rule IDs `PROV-01`, `PROV-02`, `PROV-03` (referenced by Task 8 anti-drift test, Task 10 INVARIANTS index, Task 11 DoD).

- [ ] **Step 1: Write `STANDARD.md`**

```markdown
---
name: provenance
id-prefix: PROV
status: active
applies-to:
  - "src/PinballWizard.Core/**"
  - "src/PinballWizard.Infrastructure/Scraping/**"
  - "src/PinballWizard.Infrastructure/Persistence/**"
  - "src/PinballWizard.Infrastructure/Rag/**"
---

# Provenance Standard

Every captured item must trace back to its source URL. The provenance chain
is the foundation of Phase 2 RAG citations.

**RULE PROV-01** (source-url-traceable)
WHEN:   a data path constructs, maps, or persists a ScrapedItem / catalog entry / RAG chunk
THEN:   Source, DiscoveryUrl, DiscoveryContext, GameSlug travel with the record end-to-end
NEVER:  drop or null a provenance field in a DTO projection or mapping
CHECK:  (qualitative — /local-review) — inspect new mappers/DTOs for dropped Source/DiscoveryUrl/DiscoveryContext/GameSlug
SEV:    🔴
REF:    INVARIANTS#1 · ADR-0002 · ADR-0004

**RULE PROV-02** (deterministic-id)
WHEN:   a new captured item type is introduced
THEN:   its ID is SHA-256(canonical_url.ToLower())[0:16] with the doc_/mch_ prefix
NEVER:  use a random GUID or a non-URL-derived ID for a captured item
CHECK:  rg -n "Guid.NewGuid|Random" src/PinballWizard.Infrastructure/Scraping/ src/PinballWizard.Infrastructure/Persistence/
SEV:    🔴
REF:    INVARIANTS#1 · ADR-0002

**RULE PROV-03** (catalog-contract-boundary)
WHEN:   code reads or writes the Phase1↔Phase2 boundary (catalog.json, machines / ingestion_sources containers)
THEN:   treat it as the locked API contract — additive fields only, provenance preserved
NEVER:  reshape or strip the catalog contract to suit a consumer
CHECK:  (qualitative — /local-review) — verify catalog/machines/ingestion_sources schema changes are additive
SEV:    ⚠️
REF:    INVARIANTS#8

## Definition of Done

- PROV-01: new/changed mappers carry all four provenance fields end-to-end.
- PROV-02: no `Guid.NewGuid`/`Random` ID generation for captured items.
- PROV-03: catalog-boundary changes are additive and provenance-preserving.
```

- [ ] **Step 2: Write `REQUIREMENTS.md`**

```markdown
# provenance — requirements index

| ID | slug | WHEN (summary) | SEV | REF |
|---|---|---|---|---|
| PROV-01 | source-url-traceable | map/persist a captured item | 🔴 | INVARIANTS#1 · ADR-0002/0004 |
| PROV-02 | deterministic-id | new captured item type | 🔴 | INVARIANTS#1 · ADR-0002 |
| PROV-03 | catalog-contract-boundary | read/write the Phase1↔2 boundary | ⚠️ | INVARIANTS#8 |
```

- [ ] **Step 3: Verify parse contract**

Run: `rg -n "^\*\*RULE PROV-\d{2}\*\* \([a-z0-9-]+\)$" .claude/standards/provenance/STANDARD.md`
Expected: exactly 3 matches (PROV-01, PROV-02, PROV-03).

- [ ] **Step 4: Verify every rule is in REQUIREMENTS.md**

Run: `for id in PROV-01 PROV-02 PROV-03; do rg -q "$id" .claude/standards/provenance/REQUIREMENTS.md && echo "$id ok" || echo "$id MISSING"; done`
Expected: three `ok` lines.

- [ ] **Step 5: Commit**

```bash
git add .claude/standards/provenance/
git commit -m "feat(standards) provenance standard (PROV-01..03)"
```

---

## Task 3: polite-scraping standard

**Files:**
- Create: `.claude/standards/polite-scraping/STANDARD.md`
- Create: `.claude/standards/polite-scraping/REQUIREMENTS.md`

**Interfaces:**
- Produces: `POLITE-01..04`.

- [ ] **Step 1: Write `STANDARD.md`**

```markdown
---
name: polite-scraping
id-prefix: POLITE
status: active
applies-to:
  - "src/PinballWizard.Infrastructure/Scraping/**"
---

# Polite-Scraping Standard

Polite-by-construction is a marketing surface: visibly throttle, honor
robots.txt, prefer machine-consumer metadata. Politeness > performance.

**RULE POLITE-01** (gate-routing)
WHEN:   scraper code makes an outbound HTTP request
THEN:   route it through IPolitenessGate via PoliteScraperBase (GetStringPolitelyAsync / SendPolitelyAsync)
NEVER:  call HttpClient.GetAsync / GetStringAsync / SendAsync directly in scraper code
CHECK:  rg -n "\.(GetAsync|GetStringAsync|PostAsync|SendAsync)\(" src/PinballWizard.Infrastructure/Scraping/ | rg -v "Politely|PoliteScraperBase"
SEV:    🔴
REF:    INVARIANTS#2 · feedback_polite_scraping

**RULE POLITE-02** (robots-unconditional)
WHEN:   adding or modifying a source scraper
THEN:   honor robots.txt unconditionally; sites with Disallow:/ stay skipped until explicit permission
NEVER:  add a robots.txt bypass or an override flag that ignores Disallow
CHECK:  rg -ni "ignore.*robots|robots.*bypass|disallow.*override" src/PinballWizard.Infrastructure/Scraping/
SEV:    🔴
REF:    INVARIANTS#2

**RULE POLITE-03** (metadata-first)
WHEN:   extracting structured data from a source page
THEN:   exhaust OG / JSON-LD / sitemap / robots before reaching for DOM selectors
NEVER:  hand-roll DOM scraping when a machine-consumer metadata source is available
CHECK:  (qualitative — /local-review) — confirm JsonLd/OpenGraph/sitemap tried before DOM heuristics
SEV:    ⚠️
REF:    INVARIANTS#3 · feedback_machine_consumer_metadata_first

**RULE POLITE-04** (polite-base)
WHEN:   adding a new ISourceScraper
THEN:   extend PoliteScraperBase and set the polite User-Agent + default robots.txt path on the typed HttpClient
NEVER:  introduce a scraper that bypasses PoliteScraperBase
CHECK:  rg -n "class \w+Scraper" src/PinballWizard.Infrastructure/Scraping/ then confirm each extends PoliteScraperBase
SEV:    🔴
REF:    INVARIANTS#2

## Definition of Done

- POLITE-01: no bare HttpClient verb calls in scraper code (grep clean).
- POLITE-02: no robots bypass introduced.
- POLITE-03: metadata sources tried before DOM.
- POLITE-04: new scraper extends PoliteScraperBase.
```

- [ ] **Step 2: Write `REQUIREMENTS.md`**

```markdown
# polite-scraping — requirements index

| ID | slug | WHEN (summary) | SEV | REF |
|---|---|---|---|---|
| POLITE-01 | gate-routing | scraper makes an HTTP request | 🔴 | INVARIANTS#2 |
| POLITE-02 | robots-unconditional | add/modify a scraper | 🔴 | INVARIANTS#2 |
| POLITE-03 | metadata-first | extract structured data | ⚠️ | INVARIANTS#3 |
| POLITE-04 | polite-base | add a new ISourceScraper | 🔴 | INVARIANTS#2 |
```

- [ ] **Step 3: Verify parse contract**

Run: `rg -n "^\*\*RULE POLITE-\d{2}\*\* \([a-z0-9-]+\)$" .claude/standards/polite-scraping/STANDARD.md`
Expected: 4 matches.

- [ ] **Step 4: Verify the POLITE-01 CHECK runs clean against current code**

Run: `rg -n "\.(GetAsync|GetStringAsync|PostAsync|SendAsync)\(" src/PinballWizard.Infrastructure/Scraping/ | rg -v "Politely|PoliteScraperBase" || echo "CLEAN"`
Expected: `CLEAN` (proves the CHECK command is real and currently passing). If hits appear, capture them — they are pre-existing violations to report, not plan failures.

- [ ] **Step 5: Commit**

```bash
git add .claude/standards/polite-scraping/
git commit -m "feat(standards) polite-scraping standard (POLITE-01..04)"
```

---

## Task 4: persistence-cosmos standard

**Files:**
- Create: `.claude/standards/persistence-cosmos/STANDARD.md`
- Create: `.claude/standards/persistence-cosmos/REQUIREMENTS.md`

**Interfaces:**
- Produces: `COSMOS-01..04`.

- [ ] **Step 1: Write `STANDARD.md`**

```markdown
---
name: persistence-cosmos
id-prefix: COSMOS
status: active
applies-to:
  - "src/PinballWizard.Infrastructure/Persistence/**"
  - "infra/**/*.bicep"
---

# Persistence (Cosmos) Standard

Schema CRUD via ARM; item CRUD via the data-plane SDK. Reads follow the
ADR-0036 tier model. Cosmos tuning per ADR-0025.

**RULE COSMOS-01** (arm-schema-dataplane-items)
WHEN:   provisioning Cosmos schema or performing runtime item CRUD
THEN:   schema (databases/containers/PK/throughput) goes through ARM (Azure.ResourceManager.CosmosDB); item CRUD goes through Microsoft.Azure.Cosmos
NEVER:  declare a Cosmos container in Bicep
CHECK:  rg -ni "Microsoft.DocumentDB/databaseAccounts/.*/containers|resource .* containers" infra/
SEV:    🔴
REF:    INVARIANTS#4 · ADR-0012

**RULE COSMOS-02** (read-tier-model)
WHEN:   adding a Cosmos read
THEN:   use T0 keyed read / T1 partition-aligned / T2 bounded-justified cross-partition / T3 change-feed projection; cross-partition goes through IRepository<T>.StreamCrossPartitionAsync and is listed in CrossPartitionQueryAllowListTests
NEVER:  add an ad-hoc cross-partition scan on a user-facing or unbounded-aggregate path
CHECK:  dotnet test --filter "FullyQualifiedName~CrossPartitionQueryAllowListTests" --nologo
SEV:    🔴
REF:    INVARIANTS#18 · ADR-0036

**RULE COSMOS-03** (metrics-wrapper)
WHEN:   adding a repo method that calls the Cosmos SDK
THEN:   route it through CosmosRepository<T>.ExecuteWithMetricsAsync so RU + duration land on pinwiz.cosmos.*
NEVER:  call the Cosmos SDK directly from a repo method without the metrics wrapper
CHECK:  (qualitative — /local-review) — new repo method bypassing ExecuteWithMetricsAsync
SEV:    ⚠️
REF:    INVARIANTS#13 · ADR-0025

**RULE COSMOS-04** (write-tuning)
WHEN:   adding a Container registration / write-heavy container / write path
THEN:   write-heavy container has a selective indexing policy; new container has a documented TTL decision; EnableContentResponseOnWrite=false unless the caller consumes the body; 2nd writer of a single-writer container uses ItemRequestOptions.IfMatchEtag
NEVER:  default-index a write-heavy container or re-introduce EnableContentResponseOnWrite=true without a body consumer
CHECK:  (qualitative — /local-review) — verify indexing policy, TTL decision, ETag, EnableContentResponseOnWrite against ADR-0025
SEV:    ⚠️
REF:    INVARIANTS#13 · ADR-0025

## Definition of Done

- COSMOS-01: no Cosmos container declared in Bicep (grep clean).
- COSMOS-02: CrossPartitionQueryAllowListTests passes; new cross-partition call sites allow-listed.
- COSMOS-03: new repo methods route through ExecuteWithMetricsAsync.
- COSMOS-04: indexing/TTL/ETag/EnableContentResponseOnWrite verified.
```

- [ ] **Step 2: Write `REQUIREMENTS.md`**

```markdown
# persistence-cosmos — requirements index

| ID | slug | WHEN (summary) | SEV | REF |
|---|---|---|---|---|
| COSMOS-01 | arm-schema-dataplane-items | provision schema / item CRUD | 🔴 | INVARIANTS#4 · ADR-0012 |
| COSMOS-02 | read-tier-model | add a Cosmos read | 🔴 | INVARIANTS#18 · ADR-0036 |
| COSMOS-03 | metrics-wrapper | add a repo method | ⚠️ | INVARIANTS#13 · ADR-0025 |
| COSMOS-04 | write-tuning | add container/write path | ⚠️ | INVARIANTS#13 · ADR-0025 |
```

- [ ] **Step 3: Verify parse contract**

Run: `rg -n "^\*\*RULE COSMOS-\d{2}\*\* \([a-z0-9-]+\)$" .claude/standards/persistence-cosmos/STANDARD.md`
Expected: 4 matches.

- [ ] **Step 4: Verify the COSMOS-02 CHECK names a real test**

Run: `rg -l "class CrossPartitionQueryAllowListTests" tests/`
Expected: one file path (proves the CHECK targets a real test).

- [ ] **Step 5: Commit**

```bash
git add .claude/standards/persistence-cosmos/
git commit -m "feat(standards) persistence-cosmos standard (COSMOS-01..04)"
```

---

## Task 5: observability-and-honest-failure standard

**Files:**
- Create: `.claude/standards/observability-and-honest-failure/STANDARD.md`
- Create: `.claude/standards/observability-and-honest-failure/REQUIREMENTS.md`

**Interfaces:**
- Produces: `OBS-01..04`.

- [ ] **Step 1: Write `STANDARD.md`**

```markdown
---
name: observability-and-honest-failure
id-prefix: OBS
status: active
applies-to:
  - "src/**/*.cs"
---

# Observability & Honest-Failure Standard

Observability and operability are first-class. Fallbacks must not hide
failures. The system should look healthy from a dashboard, not just from
green tests.

**RULE OBS-01** (no-masking-fallback)
WHEN:   a code path has a degraded or fallback branch
THEN:   the degradation is visible to the user and never presents synthetic/placeholder/stale content as real output
NEVER:  convert a transport/primary failure into fabricated success (the 2026-06-11 "Hello world!" leak)
CHECK:  (qualitative — /local-review) — ask "if the primary path silently died, would anyone know?"
SEV:    🔴
REF:    INVARIANTS#17 · incident-2026-06-11 · PR#363

**RULE OBS-02** (health-endpoints)
WHEN:   adding or modifying a hosted service (Api / Web / Worker)
THEN:   /healthz and /alive remain exposed via ServiceDefaults
NEVER:  remove an existing health endpoint from a deployed app
CHECK:  rg -n "MapDefaultEndpoints|/healthz|/alive" src/
SEV:    🔴
REF:    INVARIANTS#17 (hard exception) · ServiceDefaults

**RULE OBS-03** (no-secrets-in-logs)
WHEN:   adding a log statement
THEN:   log structured context only — never secrets, tokens, connection strings, PII, or a raw entity/request object
NEVER:  interpolate a secret/PII value or a raw request object into a log message
CHECK:  rg -ni "log.*(password|token|connectionstring|secret|apikey)" src/
SEV:    🔴
REF:    INVARIANTS#17 (hard exception) · local-review cat 8

**RULE OBS-04** (metered-degradation)
WHEN:   a fallback/degraded path executes OR a Cosmos/AI call is made
THEN:   log + meter the underlying failure/latency so it can be root-caused (pinwiz.* instruments)
NEVER:  swallow a failure silently or drop it from telemetry
CHECK:  (qualitative — /local-review) — fallback path increments a meter / writes a structured error
SEV:    🔴
REF:    INVARIANTS#17

## Definition of Done

- OBS-01: degraded paths are visible; no fabricated success.
- OBS-02: health endpoints intact.
- OBS-03: no secret/PII in logs (grep clean).
- OBS-04: failures are logged + metered.
```

- [ ] **Step 2: Write `REQUIREMENTS.md`**

```markdown
# observability-and-honest-failure — requirements index

| ID | slug | WHEN (summary) | SEV | REF |
|---|---|---|---|---|
| OBS-01 | no-masking-fallback | degraded/fallback branch | 🔴 | INVARIANTS#17 |
| OBS-02 | health-endpoints | add/modify hosted service | 🔴 | INVARIANTS#17 |
| OBS-03 | no-secrets-in-logs | add a log statement | 🔴 | INVARIANTS#17 |
| OBS-04 | metered-degradation | fallback/Cosmos/AI call | 🔴 | INVARIANTS#17 |
```

- [ ] **Step 3: Verify parse contract**

Run: `rg -n "^\*\*RULE OBS-\d{2}\*\* \([a-z0-9-]+\)$" .claude/standards/observability-and-honest-failure/STANDARD.md`
Expected: 4 matches.

- [ ] **Step 4: Commit**

```bash
git add .claude/standards/observability-and-honest-failure/
git commit -m "feat(standards) observability-and-honest-failure standard (OBS-01..04)"
```

---

## Task 6: testing standard

**Files:**
- Create: `.claude/standards/testing/STANDARD.md`
- Create: `.claude/standards/testing/REQUIREMENTS.md`

**Interfaces:**
- Produces: `TEST-01..04`.

- [ ] **Step 1: Write `STANDARD.md`**

```markdown
---
name: testing
id-prefix: TEST
status: active
applies-to:
  - "tests/**"
  - "src/PinballWizard.Core/ISourceScraper.cs"
  - "src/PinballWizard.Infrastructure/Scraping/**"
---

# Testing Standard

Tests assert behavior, not structure. A test named "deduplicates" must
include a fixture where dedup actually fires. Coverage is necessary but not
sufficient — tests are documentation of intent.

**RULE TEST-01** (behavior-not-structure)
WHEN:   adding or changing a test
THEN:   the test exercises behavior — a test named for an effect includes a fixture where that effect fires
NEVER:  write a test that merely restates the code's structure or asserts a constant
CHECK:  (qualitative — /local-review) — verify the named behavior is actually triggered by the fixture
SEV:    🔴
REF:    quality-spec · local-review cat 2

**RULE TEST-02** (source-alias-contract)
WHEN:   adding a new ISourceScraper
THEN:   SourceAliasContractTests pins the scraper Name to its --source alias and passes
NEVER:  add a scraper without the alias contract test green
CHECK:  dotnet test --filter "FullyQualifiedName~SourceAliasContractTests" --nologo
SEV:    🔴
REF:    CLAUDE.md (CLI) · PR-AUDIT#4

**RULE TEST-03** (sibling-no-drift)
WHEN:   a test is copied from a sibling scraper/repository test
THEN:   diff against the sibling for TryExtract wrappers, error boundaries, yield/break semantics, ctor null-checks, unused fields
NEVER:  copy a sibling test and leave drifted error-handling or assertions
CHECK:  (qualitative — /local-review) — sibling diff
SEV:    ⚠️
REF:    PR-AUDIT#2 · local-review cat 4

**RULE TEST-04** (naming-convention)
WHEN:   naming a test method
THEN:   follow Method_State_Expectation
NEVER:  use an opaque test name that hides what is asserted
CHECK:  (qualitative — /local-review) — test-name convention
SEV:    ⚠️
REF:    quality-spec

## Definition of Done

- TEST-01: named behavior is actually triggered.
- TEST-02: SourceAliasContractTests green for new scrapers.
- TEST-03: sibling-copied tests diffed for drift.
- TEST-04: Method_State_Expectation naming.
```

- [ ] **Step 2: Write `REQUIREMENTS.md`**

```markdown
# testing — requirements index

| ID | slug | WHEN (summary) | SEV | REF |
|---|---|---|---|---|
| TEST-01 | behavior-not-structure | add/change a test | 🔴 | quality-spec |
| TEST-02 | source-alias-contract | add a new ISourceScraper | 🔴 | PR-AUDIT#4 |
| TEST-03 | sibling-no-drift | copy a sibling test | ⚠️ | PR-AUDIT#2 |
| TEST-04 | naming-convention | name a test method | ⚠️ | quality-spec |
```

- [ ] **Step 3: Verify parse contract + SourceAlias test exists**

Run: `rg -n "^\*\*RULE TEST-\d{2}\*\* \([a-z0-9-]+\)$" .claude/standards/testing/STANDARD.md && rg -l "SourceAliasContractTests" tests/`
Expected: 4 RULE matches + one test-file path.

- [ ] **Step 4: Commit**

```bash
git add .claude/standards/testing/
git commit -m "feat(standards) testing standard (TEST-01..04)"
```

---

## Task 7: delivery standard

**Files:**
- Create: `.claude/standards/delivery/STANDARD.md`
- Create: `.claude/standards/delivery/REQUIREMENTS.md`

**Interfaces:**
- Produces: `DLV-01..05`.

- [ ] **Step 1: Write `STANDARD.md`**

```markdown
---
name: delivery
id-prefix: DLV
status: active
applies-to:
  - "**/*"
---

# Delivery Standard

Identity, deploy safety, and commit/PR hygiene for controlled delivery.
This standard's globs match all files; its rules gate the commit/push of any
change.

**RULE DLV-01** (personal-identity)
WHEN:   committing
THEN:   the commit authors as Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>
NEVER:  author a commit with the work email or any non-personal identity
CHECK:  git log -1 --format='%ae'   # must equal 94459922+jkeeley2073@users.noreply.github.com
SEV:    🔴
REF:    INVARIANTS#5 · feedback_personal_identity_only

**RULE DLV-02** (deployment-stacks-only)
WHEN:   adding or modifying an infra deploy script
THEN:   deploy via az stack sub create / az stack group create
NEVER:  use az deployment sub create / az deployment group create (orphans resources)
CHECK:  rg -n "az deployment (sub|group) create" infra/scripts/
SEV:    🔴
REF:    INVARIANTS#16 · feedback_deployment_stacks_only

**RULE DLV-03** (zero-warning-build)
WHEN:   completing a code change
THEN:   the build is zero-warning; treat new warnings as bugs
NEVER:  push code that introduces a new compiler/analyzer warning
CHECK:  dotnet build PinballWizard.slnx --nologo -warnaserror
SEV:    🔴
REF:    PR-AUDIT#6

**RULE DLV-04** (conventional-commit-no-attribution)
WHEN:   writing a commit message
THEN:   use conventional format `type(scope) message`; no Claude attribution trailer
NEVER:  add a Co-Authored-By: Claude / Generated-with trailer (does not match repo history)
CHECK:  git log -1 --format='%B' | rg -i "Co-Authored-By: Claude|Generated with" && echo "VIOLATION" || echo "CLEAN"
SEV:    ⚠️
REF:    pinball-workflows · feedback_personal_identity_only

**RULE DLV-05** (no-hardcoded-sub-ids)
WHEN:   adding or modifying a runbook script
THEN:   derive subscription via `az account show --query id -o tsv`
NEVER:  hardcode a subscription UUID or instance-specific resource suffix in docs/runbooks/
CHECK:  rg -ni "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}" docs/runbooks/
SEV:    🔴
REF:    PR-AUDIT#12

## Definition of Done

- DLV-01: commit identity is the personal noreply.
- DLV-02: no bare `az deployment` in infra scripts.
- DLV-03: zero-warning build.
- DLV-04: conventional commit, no Claude attribution.
- DLV-05: no hardcoded sub IDs in runbooks.
```

- [ ] **Step 2: Write `REQUIREMENTS.md`**

```markdown
# delivery — requirements index

| ID | slug | WHEN (summary) | SEV | REF |
|---|---|---|---|---|
| DLV-01 | personal-identity | committing | 🔴 | INVARIANTS#5 |
| DLV-02 | deployment-stacks-only | infra deploy script | 🔴 | INVARIANTS#16 |
| DLV-03 | zero-warning-build | complete a code change | 🔴 | PR-AUDIT#6 |
| DLV-04 | conventional-commit-no-attribution | write a commit message | ⚠️ | pinball-workflows |
| DLV-05 | no-hardcoded-sub-ids | runbook script | 🔴 | PR-AUDIT#12 |
```

- [ ] **Step 3: Verify parse contract + DLV-01/DLV-02 CHECKs run**

Run: `rg -n "^\*\*RULE DLV-\d{2}\*\* \([a-z0-9-]+\)$" .claude/standards/delivery/STANDARD.md && git log -1 --format='%ae' && (rg -n "az deployment (sub|group) create" infra/scripts/ || echo "DLV-02 CLEAN")`
Expected: 5 RULE matches; the personal noreply email; `DLV-02 CLEAN`.

- [ ] **Step 4: Commit**

```bash
git add .claude/standards/delivery/
git commit -m "feat(standards) delivery standard (DLV-01..05)"
```

---

## Task 8: Migrate `INVARIANTS.md` to a standards index

**Files:**
- Modify: `.claude/INVARIANTS.md`

**Interfaces:**
- Consumes: rule IDs from Tasks 2–7.
- Produces: the index form the Task 9 anti-drift test asserts against (every entry links a rule or is marked `→ standard pending`).

- [ ] **Step 1: Rewrite each invariant entry to link its canonical rule(s) or mark it pending**

Replace the body of `.claude/INVARIANTS.md` (keep the title + intro) so each numbered entry is either (a) a one-line pointer into a standard, or (b) a prose stub marked `→ standard pending`. Converted entries:

```markdown
# PinballWizard — Locked Invariants

Do not relitigate these. Each has a settled ADR or incident record behind it.
Converted domains are now canonical in [`standards/`](standards/README.md);
this file is the index. Entries marked `→ standard pending` are wave-2 and
still hold their prose here.

1. **Provenance is sacred.** → `PROV-01`, `PROV-02` ([provenance](standards/provenance/STANDARD.md)).
2. **Polite-by-construction.** → `POLITE-01`, `POLITE-04` ([polite-scraping](standards/polite-scraping/STANDARD.md)).
3. **Machine-consumer metadata first.** → `POLITE-03` ([polite-scraping](standards/polite-scraping/STANDARD.md)).
4. **Schema CRUD via ARM, item CRUD via data-plane SDK.** → `COSMOS-01` ([persistence-cosmos](standards/persistence-cosmos/STANDARD.md)). ([ADR-0012](../docs/adr/0012-cosmos-arm-schema-data-plane-items.md))
5. **Personal identity only.** → `DLV-01` ([delivery](standards/delivery/STANDARD.md)).
6. **PowerShell, not Git-Bash, for Cosmos resource IDs.** → standard pending (wave-2 iac-deploy). MSYS path translation rewrites `/subscriptions/...`; use PowerShell.
7. **Phase 2 storage = AI Search Basic + Cosmos.** → standard pending (wave-2 rag-agent). NOT pgvector/Postgres, NOT AI Search Standard.
8. **Catalog is the Phase 1↔Phase 2 contract.** → `PROV-03` ([provenance](standards/provenance/STANDARD.md)).
9. **Microsoft Foundry orchestration.** → standard pending (wave-2 rag-agent). ([ADR-0014](../docs/adr/0014-microsoft-foundry-orchestration.md))
10. **Per-AIAgent model selection + per-call cost ceiling.** → standard pending (wave-2 rag-agent). ([ADR-0015](../docs/adr/0015-cost-routing-and-semantic-cache.md))
11. **Confidence-threshold refusal mandatory.** → standard pending (wave-2 rag-agent). ([ADR-0017](../docs/adr/0017-confidence-threshold-refusal.md))
12. **Code-resource agent definitions.** → standard pending (wave-2 rag-agent). ([ADR-0018](../docs/adr/0018-prompt-management.md))
13. **Cosmos for User Delight.** → `COSMOS-03`, `COSMOS-04` ([persistence-cosmos](standards/persistence-cosmos/STANDARD.md)). ([ADR-0025](../docs/adr/0025-cosmos-for-user-delight.md))
14. **User Delight Frontend and Streaming.** → standard pending (wave-2 frontend-blazor). ([ADR-0026](../docs/adr/0026-user-delight-frontend-and-streaming.md))
15. **Community-resource posture.** → standard pending (wave-2 community-posture).
16. **Deployment Stacks only.** → `DLV-02` ([delivery](standards/delivery/STANDARD.md)); full two-tier Bicep → standard pending (wave-2 iac-deploy).
17. **Fallbacks must not hide failures.** → `OBS-01`, `OBS-04` ([observability-and-honest-failure](standards/observability-and-honest-failure/STANDARD.md)). ([incident 2026-06-11](../docs/adr/0036-cosmos-read-access-standard.md))
18. **Cosmos reads follow the ADR-0036 tier model.** → `COSMOS-02` ([persistence-cosmos](standards/persistence-cosmos/STANDARD.md)). ([ADR-0036](../docs/adr/0036-cosmos-read-access-standard.md))
```

> Preserve the existing ADR hyperlinks already present in the file. For entries 7, 9–12, 14–15 retain enough of the original prose that the stub is still self-explanatory (shown abbreviated above — keep the substantive clause from the current file).

- [ ] **Step 2: Verify every entry links a rule or is marked pending**

Run: `rg -n "^\d+\." .claude/INVARIANTS.md | rg -v "RULE|→ \`?[A-Z]+-\d{2}|standard pending|PROV-|POLITE-|COSMOS-|OBS-|TEST-|DLV-" || echo "ALL ENTRIES TRACKED"`
Expected: `ALL ENTRIES TRACKED` (no numbered entry lacks either a rule reference or the pending marker).

- [ ] **Step 3: Commit**

```bash
git add .claude/INVARIANTS.md
git commit -m "refactor(standards) INVARIANTS.md becomes the standards index"
```

---

## Task 9: Anti-drift conformance test

**Files:**
- Create: `tests/PinballWizard.Core.Tests/Domain/StandardsConformanceTests.cs`

**Interfaces:**
- Consumes: `FindRepoRoot()` pattern from `DocConformanceTests` (same namespace/folder); the parse contract from Task 1; the standards from Tasks 2–7; the migrated `INVARIANTS.md` from Task 8.
- Produces: a build-time guard against rule drift.

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Core.Tests/Domain/StandardsConformanceTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Core.Tests.Domain;

/// <summary>
/// Standing-state guard for the .claude/standards system. Asserts the rule
/// namespace is well-formed: unique append-only IDs, every rule indexed in its
/// REQUIREMENTS.md, and every INVARIANTS.md entry tracked (links a rule or is
/// marked "standard pending"). Mirrors DocConformanceTests' repo-root pattern.
/// </summary>
public sealed class StandardsConformanceTests
{
    private static string StandardsDir() =>
        Path.Combine(DocConformanceTests.FindRepoRoot(), ".claude", "standards");

    // \s*$ absorbs a trailing \r on CRLF files (a bare $ after \) would not
    // match when the line ends \r\n, since ) is not immediately before $).
    private static readonly Regex RuleHeader = new(
        @"^\*\*RULE ([A-Z]+-\d{2})\*\* \(([a-z0-9-]+)\)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static IEnumerable<string> StandardFiles() =>
        Directory.EnumerateFiles(StandardsDir(), "STANDARD.md", SearchOption.AllDirectories);

    [Fact]
    public void EveryRuleId_IsUnique()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var dupes = new List<string>();

        foreach (var file in StandardFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in RuleHeader.Matches(text))
            {
                var id = m.Groups[1].Value;
                if (seen.TryGetValue(id, out var first))
                    dupes.Add($"{id} in {file} (also in {first})");
                else
                    seen[id] = file;
            }
        }

        Assert.True(dupes.Count == 0,
            "Duplicate rule IDs (IDs are append-only and unique):\n  " + string.Join("\n  ", dupes));
        Assert.NotEmpty(seen);
    }

    [Fact]
    public void EveryRule_HasARowInItsRequirementsIndex()
    {
        var orphans = new List<string>();

        foreach (var file in StandardFiles())
        {
            var dir = Path.GetDirectoryName(file)!;
            var reqPath = Path.Combine(dir, "REQUIREMENTS.md");
            Assert.True(File.Exists(reqPath), $"Missing REQUIREMENTS.md next to {file}");
            var reqText = File.ReadAllText(reqPath);

            foreach (Match m in RuleHeader.Matches(File.ReadAllText(file)))
            {
                var id = m.Groups[1].Value;
                if (!reqText.Contains(id, StringComparison.Ordinal))
                    orphans.Add($"{id} ({Path.GetFileName(dir)}) — not indexed in REQUIREMENTS.md");
            }
        }

        Assert.True(orphans.Count == 0,
            "Rules with no REQUIREMENTS.md row:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void EveryInvariantEntry_IsTracked()
    {
        var root = DocConformanceTests.FindRepoRoot();
        var invariants = File.ReadAllLines(Path.Combine(root, ".claude", "INVARIANTS.md"));

        // A numbered entry line starts with "<n>. ". It is tracked if it
        // references a real rule ID or carries the pending marker. The prefix
        // set is explicit so an ADR link (e.g. ADR-0012) is NOT mistaken for a
        // rule reference — only a genuine PROV-/POLITE-/COSMOS-/OBS-/TEST-/DLV-
        // reference counts as "links a rule".
        var entryStart = new Regex(@"^\d+\.\s", RegexOptions.Compiled);
        var ruleRef = new Regex(@"\b(PROV|POLITE|COSMOS|OBS|TEST|DLV)-\d{2}\b", RegexOptions.Compiled);

        var untracked = invariants
            .Where(l => entryStart.IsMatch(l))
            .Where(l => !ruleRef.IsMatch(l) &&
                        !l.Contains("standard pending", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(untracked.Count == 0,
            "INVARIANTS.md entries that neither link a rule nor are marked 'standard pending':\n  "
            + string.Join("\n  ", untracked));
    }
}
```

- [ ] **Step 2: Run the test to confirm it passes against the built system**

Run: `dotnet test tests/PinballWizard.Core.Tests --filter "FullyQualifiedName~StandardsConformanceTests" --nologo`
Expected: PASS (3 tests). Tasks 2–8 satisfied all preconditions.

- [ ] **Step 3: Confirm it actually catches drift**

Temporarily append a duplicate rule header to one standard:
Run: `printf '\n**RULE PROV-01** (dupe-check)\n' >> .claude/standards/provenance/STANDARD.md`
Run: `dotnet test tests/PinballWizard.Core.Tests --filter "FullyQualifiedName~StandardsConformanceTests.EveryRuleId_IsUnique" --nologo`
Expected: FAIL naming `PROV-01` as a duplicate.
Then revert: `git checkout -- .claude/standards/provenance/STANDARD.md`

- [ ] **Step 4: Re-run to confirm green after revert**

Run: `dotnet test tests/PinballWizard.Core.Tests --filter "FullyQualifiedName~StandardsConformanceTests" --nologo`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/PinballWizard.Core.Tests/Domain/StandardsConformanceTests.cs
git commit -m "test(standards) anti-drift conformance test for the standards namespace"
```

---

## Task 10: `/standards-audit` skill

**Files:**
- Create: `.claude/skills/standards-audit/SKILL.md`

**Interfaces:**
- Consumes: applicability-resolution algorithm + severity taxonomy from Task 1; CHECK commands from Tasks 2–7.
- Produces: the mechanical gate the lifecycle (Task 1 §session) and PR-AUDIT (Task 11) invoke.

- [ ] **Step 1: Write the skill**

Create `.claude/skills/standards-audit/SKILL.md`:

```markdown
---
name: standards-audit
description: Mechanical pre-commit/pre-push gate. Resolves the diff to applicable PinballWizard standards by glob, runs each applicable rule's CHECK command, and emits a verdict table. Refuses to proceed on any 🔴 fail. Runs alongside the qualitative /local-review. Invoke before any commit or push of production code.
---

# /standards-audit — mechanical standards gate

Enforcement counterpart to `/local-review`. `/local-review` judges design
qualitatively; this skill runs the deterministic CHECK commands.
Contract: [`../../standards/pinball-standards-protocol.md`](../../standards/pinball-standards-protocol.md).

## When to invoke

- Pre-commit (staged diff) and pre-push / pre-PR (branch diff), per the
  protocol's session lifecycle. Replaces the mechanical half of PR-AUDIT.

## Procedure

1. **Compute the changed-file set:**

   ```bash
   git diff --name-only origin/main...HEAD
   git diff --name-only
   git status --short
   ```

2. **Resolve applicable standards.** For each `.claude/standards/*/STANDARD.md`,
   read its frontmatter `applies-to:` globs. A standard is applicable if any
   changed file matches any glob. If none match, report
   **"clean — no governed surface touched"** and stop.

3. **Run each applicable rule's CHECK.** For every RULE block in an applicable
   `STANDARD.md`, run its `CHECK:` command. Rules whose CHECK is
   `(qualitative — /local-review)` are reported as `QUAL` (deferred to
   `/local-review`), not run here.

4. **Emit the verdict table** — one row per rule:

   ```
   === Standards Audit (branch: <branch>) ===
   RULE       SEV  RESULT  EVIDENCE
   POLITE-01  🔴   PASS    no bare HttpClient verb in Scraping/
   COSMOS-02  🔴   FAIL    CrossPartitionQueryAllowListTests: 1 failing
   TEST-02    🔴   QUAL    deferred to /local-review
   ...
   Verdict: <N> 🔴 fail / <M> ⚠️ fail / <K> pass / <Q> qual
   ==========================================
   ```

5. **Gate.** Any 🔴 FAIL ⇒ refuse to proceed; name the rule ID + evidence +
   the REF, and stop before commit/push. ⚠️ FAIL ⇒ report and require a
   one-line justification to continue.

## What this skill does NOT do

- It does not replace `/local-review` (qualitative design review) — run both.
- It does not auto-fix — it reports and gates.
- It does not score or emit a compliance % (no fleet machinery).
```

- [ ] **Step 2: Verify the skill is discoverable and references resolve**

Run: `rg -n "name: standards-audit|applies-to|Refuses|verdict" .claude/skills/standards-audit/SKILL.md && test -f .claude/standards/pinball-standards-protocol.md && echo "PROTOCOL LINK OK"`
Expected: header matches + `PROTOCOL LINK OK`.

- [ ] **Step 3: Dry-run the applicability resolution against the current branch**

Run: `git diff --name-only origin/main...HEAD | head`
Expected: lists the standards-system files; confirms `delivery` (globs `**/*`) and any path-matched standard would be selected. (Manual sanity check of the algorithm, not an automated assert.)

- [ ] **Step 4: Commit**

```bash
git add .claude/skills/standards-audit/
git commit -m "feat(standards) /standards-audit mechanical gate skill"
```

---

## Task 11: Shrink `PR-AUDIT.md` to the two-skill invocation

**Files:**
- Modify: `.claude/PR-AUDIT.md`

**Interfaces:**
- Consumes: `/standards-audit` (Task 10) + `/local-review`.

- [ ] **Step 1: Replace the 14-item checklist with the two-skill gate**

Rewrite `.claude/PR-AUDIT.md`:

```markdown
# PinballWizard — PR Self-Audit (pre-push, BLOCKING)

Before pushing any PR that adds production code, run both gates and treat 🔴
as blocking. The mechanical checklist that used to live here is now the
machine-checkable rule set under [`standards/`](standards/README.md);
`/standards-audit` runs it. Background: `memory/feedback_pre_pr_self_audit.md`.

## Step 0 — Qualitative review

Run `/local-review`. Fix every 🔴; fix-or-defer (with one-line justification)
each ⚠️. Catches design/architecture/drift a grep cannot.

## Step 1 — Mechanical standards audit

Run `/standards-audit`. It resolves the diff to applicable standards, runs
each rule's CHECK, and refuses to proceed on any 🔴 fail. This replaces the
former 14-item checklist — every item migrated to a rule:

- old items 2, 4, 5 → POLITE-*, TEST-02 / TEST-01
- old item 8 → COSMOS-02..04
- old items 6, 7 → DLV-03, DLV-01
- old items 11, 12 → DLV-02, DLV-05
- old items 1, 3, 13, 14, 9, 10 → /local-review qualitative categories +
  wave-2 standards (frontend-blazor, community-posture) when promoted

## Recording the outcome

The PR description records: `/local-review` finding counts (🔴 fixed, ⚠️
fixed/deferred) and the `/standards-audit` verdict line. The PR template at
`.github/PULL_REQUEST_TEMPLATE.md` includes these lines.
```

- [ ] **Step 2: Verify no orphaned checklist remains**

Run: `rg -n "Every option field is read|Sibling-diff|No bare|az deployment" .claude/PR-AUDIT.md || echo "CHECKLIST MIGRATED"`
Expected: `CHECKLIST MIGRATED` (the prose items are gone; their coverage now lives in rules).

- [ ] **Step 3: Commit**

```bash
git add .claude/PR-AUDIT.md
git commit -m "refactor(standards) PR-AUDIT delegates to /standards-audit + /local-review"
```

---

## Task 12: Point `/local-review` at the rule namespace

**Files:**
- Modify: `.claude/skills/local-review/SKILL.md`

**Interfaces:**
- Consumes: rule IDs from Tasks 2–7. Keeps the skill's existing structure; adds rule-ID anchors so the qualitative and mechanical passes share one namespace.

- [ ] **Step 1: Add a rule-namespace note to the skill's intro**

In `.claude/skills/local-review/SKILL.md`, after the front-matter/intro `## What it does` section, insert:

```markdown
## Relationship to the standards system

This skill is the **qualitative** half of the pre-push gate; `/standards-audit`
is the **mechanical** half. The review categories below map to the
machine-checkable rules under [`.claude/standards/`](../../standards/README.md).
When a category finds an issue, cite the governing rule ID so the qualitative
finding and the mechanical gate speak one namespace:

- cat 3 (error handling / fallbacks) → `OBS-01`, `OBS-04`
- cat 4 (sibling drift) → `TEST-03`
- cat 5 (politeness) → `POLITE-01..04`
- cat 6 (provenance) → `PROV-01..03`
- cat 8 (security smells) → `OBS-03`
- cat 11 (Cosmos surface) → `COSMOS-01..04`
- cat 2 (test quality) → `TEST-01`, `TEST-04`
```

- [ ] **Step 2: Update the closing-summary instruction to cite rule IDs**

In the review prompt's final line (currently `End with a one-line summary: "X 🔴 / Y ⚠️ / Z categories ✅"`), append:

```text
For each 🔴/⚠️ finding in a governed category, cite the rule ID (e.g. PROV-01).
```

- [ ] **Step 3: Verify the edits landed**

Run: `rg -n "standards system|cite the rule ID|OBS-01|PROV-01" .claude/skills/local-review/SKILL.md`
Expected: matches for the new section and the appended instruction.

- [ ] **Step 4: Commit**

```bash
git add .claude/skills/local-review/SKILL.md
git commit -m "refactor(standards) /local-review cites the shared rule namespace"
```

---

## Task 13: Point `CLAUDE.md` and `.claude/README.md` at the standards system

**Files:**
- Modify: `CLAUDE.md`
- Modify: `.claude/README.md`

**Interfaces:**
- Consumes: the whole system. This is the session-start entry point (CLAUDE.md is always loaded) that makes the protocol + index discoverable.

- [ ] **Step 1: Update the CLAUDE.md "Locked invariants" section**

In `CLAUDE.md`, in the `## Locked invariants (do not relitigate)` section, change the opening line to point at the standards system:

```markdown
## Locked invariants (do not relitigate)

Converted domains are canonical, machine-checkable standards under
[`.claude/standards/`](.claude/standards/README.md), governed by
[`pinball-standards-protocol.md`](.claude/standards/pinball-standards-protocol.md)
(posture: **verify before done**). The full invariant index — converted rules
plus wave-2 prose stubs — is [`.claude/INVARIANTS.md`](.claude/INVARIANTS.md).
```

(Keep the existing "Key invariants to keep top-of-mind" bullet list beneath it.)

- [ ] **Step 2: Update the CLAUDE.md "PR self-audit" section**

In `CLAUDE.md`, in the `## PR self-audit (pre-push, BLOCKING)` section, replace the body with:

```markdown
Before pushing any production-code PR: run `/local-review` (qualitative) and
`/standards-audit` (mechanical gate over the standards rule set). Treat 🔴 as
blocking. Details: [`.claude/PR-AUDIT.md`](.claude/PR-AUDIT.md). The PR
description records both outcomes.
```

- [ ] **Step 3: Add the standards row to `.claude/README.md`**

In `.claude/README.md`'s "What's in this directory" table, add a row after the `INVARIANTS.md` row:

```markdown
| `standards/` | Machine-checkable, enforcement-first domain standards (RULE blocks + per-domain REQUIREMENTS index) governed by `pinball-standards-protocol.md`. The canonical home for converted invariants; `/standards-audit` runs them. |
```

And in the config-layer "Included" table, add:

```markdown
| `standards/*`, `skills/standards-audit` | Enforcement-first standards system for autonomous-session control (verify-before-done) |
```

- [ ] **Step 4: Verify the DocConformanceTests still pass (CLAUDE.md edits didn't break project-mention guards)**

Run: `dotnet test tests/PinballWizard.Core.Tests --filter "FullyQualifiedName~DocConformanceTests" --nologo`
Expected: PASS (existing CLAUDE.md guards green — the edits touched the invariants/PR-audit prose, not the solution-layout block).

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md .claude/README.md
git commit -m "docs(standards) CLAUDE.md + .claude/README point at the standards system"
```

---

## Deferred from this plan (per spec §12)

- **`docs/standards-conformance.md` showcase artifact + `/standards-audit --report`** (spec §8, optional). Deferred to a follow-up — the enforcement spine (gate + rules + DoD) is the autonomous-control payload; the prospect-facing scorecard is additive and can land after the system has proven itself on a few PRs. Re-open as a wave-1.1 task if wanted.
- **Wave-2 standards** (rag-agent, frontend-blazor, community-posture, iac-deploy) — tracked as `→ standard pending` stubs in `INVARIANTS.md` (Task 8); each is a future plan reusing Tasks 2–7's structure.

## Final verification (run before opening the PR)

- [ ] **Full anti-drift + doc-conformance pass**

Run: `dotnet test tests/PinballWizard.Core.Tests --filter "FullyQualifiedName~StandardsConformanceTests|FullyQualifiedName~DocConformanceTests" --nologo`
Expected: all PASS.

- [ ] **Zero-warning build (DLV-03 dogfood)**

Run: `dotnet build PinballWizard.slnx --nologo -warnaserror`
Expected: build succeeds, zero warnings.

- [ ] **Self-audit dogfood**

Run `/standards-audit` then `/local-review` on this branch; record the verdicts in the PR description. (The system audits its own introduction — `delivery` applies via `**/*`; expect the markdown-only diff to surface `DLV-01/03/04` PASS and the rest as `clean — no governed code surface touched` for the C#-scoped standards apart from the one test file.)

- [ ] **Open the PR** via `gh pr create`, add + verify the `claude-code` label, and put the full PR URL in the response.
