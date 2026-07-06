# Phase 1 — Docs Refresh Pass — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the human-authored docs back to truthful by closing the 15 audited staleness gaps, landing as one docs-only PR.

**Architecture:** Pure markdown edits across the doc surface, grouped so no two work-units touch the same file. Verification is the existing `docs.yml` (link + Mermaid checks) plus the existing doc-conformance test suite, which must not regress. Decision-log entries are drafted under human supervision (this pass only) and clearly marked AI-drafted in the PR.

**Tech Stack:** Markdown; Node link-checker (`tools/docs/check-links.mjs`), Mermaid validator (`tools/docs/validate-mermaid.mjs`); xUnit doc-conformance tests.

## Global Constraints

- Personal identity only — commits author as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. No Claude attribution trailer.
- No ASCII diagrams — Mermaid only.
- No XML doc comments as a work product; do not flag missing ones.
- Provenance/politeness/no-masking invariants are not touched by doc edits, but any *description* of them must stay accurate.
- Branch: `docs/refresh-and-docs-agent` (worktree `.worktrees/docs-refresh-agent`). Never edit on `main`.
- Convert relative dates to absolute (today = 2026-07-06).
- The agent-vs-human decision-log rule is relaxed *only* for this supervised pass; entries must be marked AI-drafted in the PR description.

---

### Task 1: Live-status corrections (README + vision)

**Files:**
- Modify: `README.md` (Phase-6→7 status claims; doc-map phase line)
- Modify: `docs/vision.md` (only if it echoes a pre-live status)

**Interfaces:**
- Consumes: audit facts — `README.md:19`, `README.md:180`, `README.md:152`; `CLAUDE.md` is the source of truth ("Phases 2–6 complete… Phase 7 current").
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Read the current claims**

Run: `grep -n "Phase 5\|Phase 6\|goes live\|fully deployable\|H-chain\|Phases 0" README.md docs/vision.md`
Expected: the three README lines from the audit plus any vision echo.

- [ ] **Step 2: Rewrite README live-status to reflect shipped reality**

In `README.md`, replace the pre-live framing with the shipped state. The authoritative wording comes from `CLAUDE.md`:
- Line ~19 area: state that `pinwiz.ai` is **live** — a RAG-powered Wizard with source-cited Q&A, admin control plane, and end-to-end observability; Phases 2–6 complete; Phase 7 is the active work stream.
- Line ~152 doc-map: change "Phases 0–5 closed; Phase 6 current" → "Phases 0–6 closed; Phase 7 current".
- Line ~180 area: replace "Phase 5 is code-complete and… deployable" with the live-status sentence.

- [ ] **Step 3: Reconcile vision.md if needed**

If `docs/vision.md` states a not-yet-live posture, update to "live on pinwiz.ai". If it is already tense-neutral, leave it.

- [ ] **Step 4: Verify no broken links introduced**

Run: `node tools/docs/check-links.mjs`
Expected: exit 0, no broken links.

- [ ] **Step 5: Commit**

```bash
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -am "docs(readme) correct live-status: Phases 2-6 shipped, Phase 7 active"
```

---

### Task 2: Source tables (knowledge-sources + CLAUDE.md scraper table)

**Files:**
- Modify: `docs/knowledge-sources.md` (Tilt Forums active; Freshdesk; IPDB fields; Pinball Brothers posture)
- Modify: `CLAUDE.md` (scraper table row for Pinball Brothers Freshdesk)

**Interfaces:**
- Consumes: audit facts — ADR-0050 (Tilt Forums shipped, PRs #670/#675); PR #663 `PbFreshdeskSolutionsScraper`; commit `11a3a8d` IPDB fields on `Machine`.
- Produces: an accurate scraper roster consumed by no later task, but the CLAUDE.md edit must keep `DocConformanceTests` green.

- [ ] **Step 1: Read the current source rows**

Run: `grep -n "Tilt\|Freshdesk\|Pinball Brothers\|pinballbrothers\|IPDB\|Ipdb" docs/knowledge-sources.md CLAUDE.md`
Expected: the stale rows from the audit (Tilt gated, Pinball Brothers "future", IPDB "deferred").

- [ ] **Step 2: Update knowledge-sources.md**

- Tilt Forums: change "gated on written permission" → active ingestion, cite ADR-0050 (founder's public invitation) and PRs #670/#675.
- Pinball Brothers: change `pinballbrothers.com` "Future expansion" → active, add the Freshdesk support-portal scraper (`PbFreshdeskSolutionsScraper`, PR #663).
- IPDB: change "deferred (no API)" note to reflect that `Machine` now carries `IpdbId` + `IpdbReferenceUrl` (reference-link fields, not full ingestion) per commit `11a3a8d`.

- [ ] **Step 3: Update the CLAUDE.md scraper table**

Add a Pinball Brothers Freshdesk row to the scraper table (the table currently lists only `PbGamePageScraper`). Keep the table's existing column shape.

- [ ] **Step 4: Verify CLAUDE.md conformance still passes**

Run: `dotnet test tests/PinballWizard.Core.Tests/PinballWizard.Core.Tests.csproj --filter "FullyQualifiedName~DocConformanceTests" -v minimal`
Expected: PASS (no phantom project names introduced; the scraper table lives outside the solution-layout fenced block, so this is a safety check).

- [ ] **Step 5: Commit**

```bash
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -am "docs(sources) Tilt Forums active, Pinball Brothers Freshdesk, IPDB ref fields"
```

---

### Task 3: Build-spec + guardrails feature coverage

**Files:**
- Modify: `docs/build-spec.md` (phase entries for #681, #669, #680, #678, #671)
- Modify: `docs/guardrails.md` (locked-decisions entry for ADR-0049)

**Interfaces:**
- Consumes: audit facts — PR #681 (ACA per-scraper jobs + admin schedule editor), #669 (OCR activation), #680 (GridSearch), #678 (DocumentLinker reissue families), #671 (bugfinder), ADR-0049 (findability program).
- Produces: nothing later-consumed.

- [ ] **Step 1: Locate the relevant phase sections (do NOT read the whole 1430-line file)**

Run: `grep -n "^## Phase\|^### \|Complete\|ACA Job\|OCR\|Document Intelligence\|findability" docs/build-spec.md`
Expected: phase headings + the Phase 4.5 OCR line (~965) from the audit.

- [ ] **Step 2: Add/adjust build-spec status entries**

Read only the located sections (`sed -n`/Read with offset+limit). Add concise status entries under the appropriate phase:
- ACA per-scraper scheduled jobs (15 jobs) + `CronExpressionValidator` + admin schedule editor (PR #681).
- OCR activation of the ADI fallback (PR #669) — distinct from the Phase 4.5 W1 wiring line; note activation, not re-wiring.
- Admin GridSearch unification (PR #680).
- DocumentLinker cross-year reissue-family resolution (PR #678) + `ManufacturerSlugs` backfill (PR #676).
- Bugfinder Playwright crawler + GPT-4o UI review (PR #671) — note it is a local QA tool, not CI-gated.

- [ ] **Step 3: Add ADR-0049 to guardrails locked-decisions**

In `docs/guardrails.md` locked-decisions list, add an entry: findability + relevance-ranking routes machine lookup through Azure AI Search (ADR-0049, Accepted 2026-07-02).

- [ ] **Step 4: Verify links + Mermaid**

Run: `node tools/docs/check-links.mjs && node tools/docs/validate-mermaid.mjs`
Expected: both exit 0.

- [ ] **Step 5: Commit**

```bash
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -am "docs(build-spec) record ACA jobs, OCR activation, GridSearch, linker, bugfinder, findability"
```

---

### Task 4: Engineering-standards E2E table + stale-file triage

**Files:**
- Modify: `docs/ENGINEERING_STANDARDS.md` (§7.1 E2E suite table — add bugfinder)
- Modify: `docs/PHASE5-DRIFT-AUDIT.md`, `docs/quality-bar.md`, `docs/self-healing-agent-and-ai-roadmap.md` (mark archived/dated or refresh header)

**Interfaces:**
- Consumes: audit facts — bugfinder missing from §7.1; three May-2026 files predate Phase 7.
- Produces: nothing later-consumed.

- [ ] **Step 1: Locate the E2E suite table**

Run: `grep -n "E2E\|BugFinder\|7.1\|Playwright" docs/ENGINEERING_STANDARDS.md`

- [ ] **Step 2: Add the bugfinder row to §7.1**

Add a row for the bugfinder crawler (`tests/PinballWizard.Web.Tests/BugFinder/`, `Category=BugFinder`, local-only, GPT-4o vision UI review, non-gating reporter).

- [ ] **Step 3: Triage the three dated files**

For each of `PHASE5-DRIFT-AUDIT.md`, `quality-bar.md`, `self-healing-agent-and-ai-roadmap.md`: add a one-line header banner noting it is a point-in-time artifact dated <its date> and pointing to the current source of truth (build-spec/guardrails). Do not delete — historical value is intentional (`learning-from-failure` posture).

- [ ] **Step 4: Verify links**

Run: `node tools/docs/check-links.mjs`
Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -am "docs(standards) add bugfinder to E2E table; date-banner stale artifacts"
```

---

### Task 5: Drafted decision-log entries (supervised exception)

**Files:**
- Modify: `docs/decision-log.md` (new dated section covering June–July 2026 decisions)

**Interfaces:**
- Consumes: audit fact — no new dated section since 2026-05-30.
- Produces: entries the human reviewer accepts/edits/strikes before merge.

- [ ] **Step 1: Read the decision-log format**

Run: `sed -n '1,40p' docs/decision-log.md`
Expected: the `## YYYY-MM-DD` section format.

- [ ] **Step 2: Draft the missing entries**

Add a `## 2026-07-06 (AI-drafted — pending human confirmation)` section with short entries (decision + reasoning, per repo style) for: OCR activation rationale + why deferred from Phase 4.5; Tilt Forums posture reversal (permission-gated → active, ADR-0050); individual-job-per-scraper vs combined sweep (PR #681); Freshdesk polite-source posture change; admin GridSearch architecture; `ManufacturerSlugs` backfill approach (PR #676).

- [ ] **Step 3: Mark clearly as AI-drafted**

Ensure the section header and each entry make the AI-drafted, pending-confirmation status unmistakable.

- [ ] **Step 4: Verify links**

Run: `node tools/docs/check-links.mjs`
Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -am "docs(decision-log) AI-drafted June-July 2026 decisions for human confirmation"
```

---

### Task 6: Full-suite verification + PR

**Files:** none (verification + PR only)

- [ ] **Step 1: Run the CI-equivalent doc gates**

Run: `node tools/docs/check-links.mjs && node tools/docs/validate-mermaid.mjs`
Expected: both exit 0.

- [ ] **Step 2: Run doc + standards conformance tests**

Run: `dotnet test tests/PinballWizard.Core.Tests/PinballWizard.Core.Tests.csproj --filter "FullyQualifiedName~Conformance" -v minimal`
Expected: PASS.

- [ ] **Step 3: Run `/local-review` and `/standards-audit`**

Per repo PR-audit protocol. Treat 🔴 as blocking.

- [ ] **Step 4: Push + open PR**

```bash
git push -u origin docs/refresh-and-docs-agent
gh pr create --label claude-code --title "docs: refresh pass — close 15 staleness gaps" \
  --body "Closes the audited doc-staleness gaps. Decision-log entries in the final commit are AI-drafted and marked pending human confirmation — accept/edit/strike as you review. /local-review + /standards-audit: <paste outcome>."
```

- [ ] **Step 5: Verify the `claude-code` label**

Run: `gh pr view --json labels`
Expected: `claude-code` present; re-apply if missing.

## Self-Review

- **Spec coverage:** All 15 gaps map to Tasks 1–5 (live-status ×1–2; sources ×3; build-spec/guardrails ×6; standards/stale ×3; decision-log ×1). ✓
- **Placeholders:** Prose targets are specified by exact grep + audit line refs; no "add appropriate X".
- **Type consistency:** No code types in this phase.
