# Documentation Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Update every Tier 1 (showcase) and Tier 2 (operational) doc so it is factually accurate, consistently formatted, and holds up under prospect and senior-engineer scrutiny.

**Architecture:** Three sequential passes on a dedicated `docs/audit` branch (from `main`, not from `feat/wif-showcase`). Each pass covers a logical group of docs and ends with a commit. No test cycle — verification is fact-checking against the codebase.

**Tech Stack:** Markdown editing; PowerShell for fact-checking greps; `git`; `gh` for PR creation.

## Global Constraints

- Branch: `docs/audit`, created from `main`
- Formatting standard: `docs/superpowers/specs/2026-06-28-documentation-audit-design.md`
- Present tense for shipped features; future tense only for genuinely unshipped features
- No placeholder stubs — fill or remove any "TBD" / "to be created" language
- Every Tier 1 doc opens with 1–2 sentences orienting the reader on what it is and who reads it
- Internal links use relative paths; broken links corrected or removed
- Do NOT touch: `docs/superpowers/specs/`, `docs/superpowers/plans/`, `docs/ui/`, `.claude/commands/`, `.claude/skills/`, `.claude/agents/`
- Commit format: `docs(audit) pass<N>: <short description>`

## Verified facts (use these — do not re-derive)

| Fact | Correct value | Where wrong |
|---|---|---|
| Aspire version | **13.4.6** | README badge says 13.2; Tech Stack prose says 13.2 |
| Microsoft.Agents.AI version | **1.5.0** | README Tech Stack says 1.4.0 |
| ADR count | **47** (0001–0047) | README says "27 ADRs"; docs map says "0001–0027" |
| Admin control plane | **Complete** (all 6 capabilities shipped 2026-06-24) | README Known Limitations calls admin "structural placeholders" |
| PR-AUDIT process | **Two-step: `/local-review` + `/standards-audit`** | guardrails.md says "7-item mechanical" checklist |
| `docs/data-model.md` | **Does not exist** | README links to it as if it does |

---

### Task 0: Create docs/audit branch

**Files:** None — branch setup only.

- [ ] **Step 1: Verify current state**

```pwsh
git status
git rev-parse --abbrev-ref HEAD
```

Expected: working tree clean.

- [ ] **Step 2: Create branch from main**

```pwsh
git fetch origin main
git checkout main
git pull origin main
git checkout -b docs/audit
```

Expected: `Switched to a new branch 'docs/audit'`

---

### Task 1: Fix README.md

**Files:**
- Modify: `README.md`

**All known issues to fix:**

1. ADR count "27" → "47" (two locations: "What this demonstrates" bullet + documentation map)
2. ADR range "0001–0027" → "0001–0047" in the documentation map table
3. Aspire badge "13.2" → "13.4.6"
4. Aspire Tech Stack prose "13.2" → "13.4.6"
5. `Microsoft.Agents.AI` version "1.4.0" → "1.5.0" in Tech Stack
6. Admin pages described as "structural placeholders" in Known Limitations → remove that limitation (admin is complete)
7. Phase 5 status row: admin description refers to skeleton/placeholder admin → update to reflect complete admin control plane (dashboard, sources with enable/disable, machines, manufacturers, monitoring, per-source run history, corpus/RAG stats — all shipped 2026-06-24)
8. Broken link `docs/data-model.md` (file does not exist) → remove the sentence that references it
9. Documentation map table: ADR range column entry
10. "What this demonstrates" → add Silverball Labs live-pricing + shared Blazor component library bullets
11. Test count "1,564" → get current count (Step 1 below)

- [ ] **Step 1: Get current passing test count**

```pwsh
dotnet build PinballWizard.slnx -c Release -q 2>$null
dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E" --no-build --list-tests 2>$null | Select-String "^\s+\S" | Measure-Object -Line | Select-Object -ExpandProperty Lines
```

Note the number. Use it to replace "1,564" in the README.

- [ ] **Step 2: Read README.md lines 1–120 (badges through provenance model)**

```pwsh
# In your editor — read lines 1–120
```

Check: confirm badge line numbers match what you'll edit; note the exact text of the broken `data-model.md` sentence on line ~99.

- [ ] **Step 3: Read README.md lines 101–175 ("What this demonstrates" through Tech Stack)**

Note exact wording of the "27 ADRs" and "1.4.0" entries.

- [ ] **Step 4: Apply fixes — badges and version references**

In `README.md`:

Replace the Aspire badge (line ~8):
```markdown
[![Aspire](https://img.shields.io/badge/.NET%20Aspire-13.4.6-512BD4?logo=dotnet)](https://learn.microsoft.com/en-us/dotnet/aspire/)
```

In Tech Stack section, replace:
- `.NET Aspire 13.2` → `.NET Aspire 13.4.6`
- `Microsoft.Agents.AI` version `1.4.0` → `1.5.0`

- [ ] **Step 5: Apply fixes — ADR count and range**

In "What this demonstrates", find `27 ADRs for non-obvious decisions` → `47 ADRs for non-obvious decisions`.

In the documentation map table, update the ADRs row:

```markdown
| [`docs/adr/`](docs/adr/) | Architecture Decision Records (0001–0047) |
```

- [ ] **Step 6: Apply fixes — remove broken data-model.md link**

Remove the sentence ending with `See [\`docs/data-model.md\`](docs/data-model.md) for the full record type definitions.` (line ~99). The provenance model JSON example stands on its own without it.

- [ ] **Step 7: Apply fixes — "What this demonstrates" new bullets**

Add two bullets in the "AI engineering" cluster (after the existing AI engineering bullet):

```markdown
- **Live pricing integration** — Silverball Labs API provides secondary-market valuations via the `getMarketValue` agent tool; every pricing answer carries source attribution ([ADR-0045](docs/adr/0045-silverball-labs-pricing-integration.md))
- **Shared Blazor component library** — eight `App*` wrapper components in `Components/Shared/` extract repeated MudBlazor patterns across all pages; enforced by project convention tests ([ADR-0046](docs/adr/0046-shared-blazor-component-library.md))
```

- [ ] **Step 8: Apply fixes — Phase 5 status row admin description**

Find the Phase 5 row in the project status table. The admin description currently says something like "skeleton" or "placeholders". Replace with:

```markdown
| 5 — Blazor + MudBlazor frontend | ✅ Complete | Blazor Web App (auto-render mode) + SSE streaming Wizard surface; five pinball themes; citation strips; community-resource refusal panels; Entra External ID auth; complete admin control plane (AdminDashboard, AdminSources with enable/disable toggle, AdminMachines, AdminManufacturers, AdminMonitoring, per-source run history, corpus/RAG stats) |
```

- [ ] **Step 9: Apply fixes — Known Limitations v1**

Remove the `/admin` pages structural placeholder bullet entirely. It is no longer accurate.

If removing it leaves only a few limitations, verify the remaining bullets are accurate and consolidate if needed. The section should still exist with the accurate remaining limitations (RAG corpus subset, cost attribution at zero, Lighthouse CI on test environment only).

- [ ] **Step 10: Update test count**

Replace `1,564 passing` with the count from Step 1. Adjust the surrounding sentence to match (e.g., `X,XXX passing across foundation + scrapers + Cosmos...`).

- [ ] **Step 11: Formatting pass**

Read README from top to bottom once more:
- Every internal link uses relative path (not absolute URL starting with `https://github.com/...`)
- All tables have `| --- |` separator rows
- No future-tense prose for shipped features
- No "TBD" or placeholder stubs remaining

- [ ] **Step 12: Commit**

```pwsh
git add README.md
git commit -m "docs(audit) pass1: fix README — ADR count/range, Aspire/Agents versions, admin complete, broken link, new capabilities, test count"
```

---

### Task 2: Fix docs/vision.md; verify docs/adr/README.md

**Files:**
- Modify: `docs/vision.md` (conditional on Step 1 below)
- Verify: `docs/adr/README.md` (expected already current — confirm only)

- [ ] **Step 1: Check whether mutation testing (Stryker) is configured**

```pwsh
Get-ChildItem -Recurse -Filter "stryker*" -ErrorAction SilentlyContinue | Select-Object FullName
Select-String -Path "Directory.Build.props","Directory.Packages.props" -Pattern "stryker" -ErrorAction SilentlyContinue
```

If Stryker is NOT found: in `docs/vision.md`, find and remove "validated by mutation testing" from the clean-architecture bullet. If Stryker IS found: leave the claim as-is.

- [ ] **Step 2: Read docs/vision.md in full**

Check every claim:
- Is the orientation paragraph ("PinballWizard / pinwiz.ai is a public reference application...") accurate?
- Are all capability bullets ("Cloud-native architecture", "AI engineering", etc.) accurate as of today?
- Future-tense items (passport, social login, strategy tracker) — are they genuinely still unshipped? (Yes — leave as future tense.)
- No broken links, no "TBD" stubs

- [ ] **Step 3: Apply all fixes to docs/vision.md**

Apply only what Step 1 and Step 2 surfaced. Do not rewrite sections that are accurate.

- [ ] **Step 4: Verify docs/adr/README.md has all 47 entries**

```pwsh
(Get-ChildItem docs/adr/ -Filter "*.md" | Where-Object Name -ne "README.md").Count
```

Expected: 47. If the count differs, find the gap (compare file list against the README index table) and add the missing row. If count matches 47 and all filenames align, no edit needed.

- [ ] **Step 5: Commit**

```pwsh
git add docs/vision.md docs/adr/README.md
git commit -m "docs(audit) pass1: fix vision.md mutation testing claim; confirm ADR index complete"
```

If `docs/adr/README.md` needed no changes, omit it from the commit (or include it as a no-op — either is fine).

---

### Task 3: Fix CONTRIBUTING.md and SECURITY.md

**Files:**
- Modify: `CONTRIBUTING.md`
- Modify: `SECURITY.md`

- [ ] **Step 1: Read CONTRIBUTING.md in full**

Check:
- Test filter command matches the canonical: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
- Aspire version references (update any "13.x" to "13.4.6")
- Branch and PR workflow instructions accurate to pinball-workflows rule (no `az repos`, only `gh pr create`)
- No "TBD" stubs
- Opening orientation sentence present (add if missing: "This guide covers local development setup, test conventions, and the PR workflow for PinballWizard.")

- [ ] **Step 2: Apply fixes to CONTRIBUTING.md**

- [ ] **Step 3: Read SECURITY.md in full**

Check:
- Contact/disclosure process is accurate (`jim@earlybirdsolutions.com` or GitHub private vulnerability report)
- No stale version scope claims
- Opening orientation sentence present (add if missing)
- No "TBD" stubs

- [ ] **Step 4: Apply fixes to SECURITY.md**

- [ ] **Step 5: Commit**

```pwsh
git add CONTRIBUTING.md SECURITY.md
git commit -m "docs(audit) pass1: fix CONTRIBUTING and SECURITY — orientation, Aspire version, test filter"
```

---

### Task 4: Fix docs/guardrails.md

**Files:**
- Modify: `docs/guardrails.md`

**Known issues:**
1. Line 85: `c:\projects\CLAUDE.md` — old path; repo moved to `c:\earlybird\PinballWizard`
2. "7-item mechanical" self-audit — PR-AUDIT is now a two-step process (`/local-review` + `/standards-audit`), not a numbered checklist
3. Risk R12: "placeholder ACA image" — verify whether ACA is now serving the real app
4. Risk register: all "Last reviewed" dates are 2026-05-04 or 2026-05-13; update resolved/reviewed risks to 2026-06-28

- [ ] **Step 1: Check current ACA deployment state (R12)**

```pwsh
# Look for evidence of the Wizard ACA app definition in Bicep
Select-String "wizard" infra/modules/shared.bicep | Select-Object Line | Select-Object -First 10
# Check recent deploy-related commits
git log --oneline --grep="aca\|container app\|wizard app\|deploy" -15
```

If the Wizard ACA app is defined in Bicep and deployed (not just the placeholder image), update R12 to Resolved. If still on placeholder image, update Last reviewed only.

- [ ] **Step 2: Read docs/guardrails.md in full**

Additional things to check beyond known issues:
- Any other stale path references
- Any "TBD" stubs
- The locked-decisions list — are all 16+ locked decisions still accurate? (Spot-check: do the ADR numbers cited still match the actual ADRs?)
- Spec maintenance table — are all docs listed? (Check: `docs/ai-development-model.md`, `docs/learning-from-failure.md`, and any new docs added since guardrails.md was last updated may need rows)

- [ ] **Step 3: Fix path reference**

Find `c:\projects\CLAUDE.md` (line ~85) and replace with a relative reference:
```markdown
Per [`CLAUDE.md`](../CLAUDE.md) § "Executing actions with care" and the seven main goals.
```

(The path is relative from `docs/guardrails.md` to the repo root `CLAUDE.md`.)

- [ ] **Step 4: Fix self-audit description**

Find "7-item mechanical" in the Per-PR gate section. Replace the description with:

```markdown
2. `/standards-audit` — resolves the diff to applicable standards, runs each rule's CHECK, and refuses to proceed on any 🔴. Replaces the former numbered checklist; every old item is now a named rule in `.claude/standards/`.
```

Also update the per-PR gate summary list to remove "7-item mechanical checklist" and replace with "Standards audit (`/standards-audit`) — all applicable 🔴 pass".

- [ ] **Step 5: Update risk R12 and review dates**

Based on Step 1 findings:

If R12 is resolved:
```markdown
| R12 | `pinwiz.ai` live surface not yet serving real app (placeholder ACA image) | High | **Resolved 2026-06-28** | Wizard ACA app definition shipped in Bicep; real image deployed. | 2026-06-28 |
```

If R12 is still open (placeholder image):
Update Last reviewed to `2026-06-28`.

Update Last reviewed for R7, R8, R9, R10, R13 to `2026-06-28` since they are being reviewed now.

- [ ] **Step 6: Formatting pass**

Verify all tables have `| --- |` separators; all ADR cross-references use relative paths; no bare `\` backslash path notation elsewhere.

- [ ] **Step 7: Commit**

```pwsh
git add docs/guardrails.md
git commit -m "docs(audit) pass2: fix guardrails — stale path, self-audit description, risk register dates"
```

---

### Task 5: Fix docs/build-spec.md (targeted sections only)

**Files:**
- Modify: `docs/build-spec.md`

This file is 1,365 lines. Read only the sections that need updating; leave the rest untouched.

- [ ] **Step 1: Read Phase 5 section (lines 974–1033)**

Check:
- Admin control plane description — Phase 5 retrospective mentions admin pages but may not reflect the full post-Phase-5 admin capabilities (dashboard, sources with enable/disable, machines, manufacturers, monitoring, run history, corpus stats — all merged by 2026-06-24 in subsequent PRs)
- Look for any "placeholder" language about admin that needs updating
- "Phase 5 exit criteria not formally gated" note on line 1024 — is that gap now addressed?

- [ ] **Step 2: Read Phase 6 section (lines 1035–1333)**

Check:
- Status line: currently "🟡 H-chain complete; 3 gates deferred to Phase 7" — confirm this is still accurate
- Wave 1 / Wave 2 completion states
- Any items marked pending that have since shipped

- [ ] **Step 3: Read Phase 7+ section (lines 1334–end)**

Check:
- Any deferred features that have since shipped should be moved to their phase
- Any new deferrals to add

- [ ] **Step 4: Apply targeted fixes**

Edit only the sections read in Steps 1–3. Do not rewrite sections you did not read.

Key fix: if Phase 5 "What shipped" section refers to admin as skeleton/placeholder, add a note or update to reference the post-Phase-5 admin work:

```markdown
#### Post-Phase-5 admin capabilities (PRs merged 2026-06-24)

Six admin capabilities shipped after Phase 5 close: AdminDashboard with live source metrics, AdminSources with per-source enable/disable toggle and detail drilldown, AdminMachines catalog browser, AdminManufacturers, AdminMonitoring (latency/5xx/alert-rules tiles live-wired via `IMonitoringStatsReader`; cost tile eval-only pending `agent-framework#2688`), and per-source scrape run history with corpus/RAG stats. Full admin control plane is now complete.
```

- [ ] **Step 5: Commit**

```pwsh
git add docs/build-spec.md
git commit -m "docs(audit) pass2: fix build-spec — admin completeness, Phase 5/6 accuracy"
```

---

### Task 6: Fix docs/quality-spec.md

**Files:**
- Modify: `docs/quality-spec.md`

- [ ] **Step 1: Read docs/quality-spec.md in full**

For each gate or tool described, check:
- Is it described as "planned" when it has actually shipped?
- Are version numbers current? (Playwright, .NET, Aspire)
- Are there any "TBD" stubs?
- Is the opening orientation sentence present?

- [ ] **Step 2: Apply all fixes**

Common patterns to look for and fix:
- Any gate described as "Phase N" that is now complete — update status
- `Aspire 13.x` → `13.4.6`
- Any reference to a 7-item or 14-item PR checklist → update to `/local-review` + `/standards-audit` two-step

- [ ] **Step 3: Commit**

```pwsh
git add docs/quality-spec.md
git commit -m "docs(audit) pass2: fix quality-spec — gate statuses, tool versions, checklist reference"
```

---

### Task 7: Fix docs/architecture-v2.md, docs/ai-development-model.md, docs/learning-from-failure.md

**Files:**
- Modify: `docs/architecture-v2.md` (if needed)
- Modify: `docs/ai-development-model.md` (if needed)
- Modify: `docs/learning-from-failure.md` (if needed)

- [ ] **Step 1: Read docs/architecture-v2.md**

This is the forward-direction vision doc. Check:
- Is it clearly framed as forward direction, not current state? If not, add a callout at the top: `> This document describes the forward-direction architecture. The shipped implementation is described in [\`docs/build-spec.md\`](build-spec.md).`
- Phase numbering: the doc uses its own phase numbering that diverges from build-spec. Confirm there is a note explaining this (CLAUDE.md says: "Where the v2 doc references 'Phase 2,' our build-spec phasing has Phase 2 closed and Phase 4 current"). If no note exists, add one.
- Any overclaims (stating as current something that is still future)
- Broken links

- [ ] **Step 2: Read docs/ai-development-model.md**

Check:
- Orientation sentence at top
- Process description matches current practice (spec → plan → TDD → `/local-review` + `/standards-audit` → CI → CodeQL → PR)
- No stale step names or tool references
- No "TBD" stubs

- [ ] **Step 3: Read docs/learning-from-failure.md**

Check:
- Orientation sentence at top
- All case studies reference real incidents (not hypothetical)
- The failure→memory→guardrail loop description is accurate
- No "TBD" stubs

- [ ] **Step 4: Apply all fixes**

Apply only what Steps 1–3 surfaced. Preserve the content of these docs — they are well-established; this is an accuracy and formatting pass, not a rewrite.

- [ ] **Step 5: Commit**

```pwsh
git add docs/architecture-v2.md docs/ai-development-model.md docs/learning-from-failure.md
git commit -m "docs(audit) pass2: fix architecture-v2, ai-development-model, learning-from-failure — orientation, framing, accuracy"
```

---

### Task 8: Fix CLAUDE.md, .claude/INVARIANTS.md, .claude/PR-AUDIT.md

**Files:**
- Modify: `CLAUDE.md`
- Modify: `.claude/INVARIANTS.md` (if needed)
- Modify: `.claude/PR-AUDIT.md` (if needed)

- [ ] **Step 1: Verify shared Blazor component library contents**

```pwsh
Get-ChildItem src/PinballWizard.Web/Components/Shared/ -Filter "App*.razor" | Select-Object BaseName | Sort-Object BaseName
```

Compare the output against the component table in `CLAUDE.md`. The table should list exactly the components that exist. Add any missing; remove any that no longer exist.

- [ ] **Step 2: Read CLAUDE.md in full**

Check:
- Aspire version in "Aspire foundation" section — update to 13.4.6 if not already
- All 10 `ISourceScraper` entries in the source manufacturers table still match current scrapers
- `Microsoft Agent Framework (`Microsoft.Agents.AI` 1.4.0)` in locked invariants or tech references → update to 1.5.0
- ADR range: CLAUDE.md says "don't hardcode the numeric range" — verify it follows its own rule (links to the ADR README by path, not a hardcoded range)
- Shared component table matches Step 1 output
- Any stale phase descriptions

- [ ] **Step 3: Apply fixes to CLAUDE.md**

Apply all changes found in Steps 1–2.

- [ ] **Step 4: Read .claude/INVARIANTS.md**

Spot-check each invariant against the codebase. Greps to run:

```pwsh
# Polite scraping invariant
Select-String "PoliteScraperBase" src/ -Recurse -Include "*.cs" | Select-Object -First 3
# Deterministic ID invariant
Select-String "doc_" src/PinballWizard.Infrastructure/ -Recurse -Include "*.cs" | Select-Object -First 3
# ARM schema / data-plane split
Select-String "ArmCosmosProvisioner" src/ -Recurse -Include "*.cs" | Select-Object -First 3
# Deployment Stacks invariant
Select-String "az stack" infra/scripts/ -Recurse | Select-Object -First 3
```

If any invariant no longer matches the code, update the invariant text to reflect current reality. Do NOT delete invariants — supersede them with a note.

- [ ] **Step 5: Read .claude/PR-AUDIT.md**

Verify:
- The two-step description (`/local-review` then `/standards-audit`) is accurate
- No stale items remain from the old numbered checklist format
- The standards migration notes (old items 2, 4, 5 → TEST-03, etc.) are still accurate

Apply any corrections.

- [ ] **Step 6: Commit**

```pwsh
git add CLAUDE.md .claude/INVARIANTS.md .claude/PR-AUDIT.md
git commit -m "docs(audit) pass2: fix CLAUDE.md Agents version + shared components; verify INVARIANTS and PR-AUDIT"
```

---

### Task 9: Fix docs/runbooks/ (all 8 files)

**Files:**
- Modify: `docs/runbooks/README.md`
- Modify: `docs/runbooks/01-incident-response.md`
- Modify: `docs/runbooks/02-cost-anomaly.md`
- Modify: `docs/runbooks/03-cosmos-restore.md`
- Modify: `docs/runbooks/04-ai-search-rebuild.md`
- Modify: `docs/runbooks/05-secret-rotation.md`
- Modify: `docs/runbooks/06-source-site-outage.md`
- Modify: `docs/runbooks/h-chain-operator-runbook.md`

- [ ] **Step 1: Read docs/runbooks/README.md**

Verify it lists all 8 files (01–06 + h-chain). The entry for each runbook should have a one-line description of its purpose. Add any missing entries.

- [ ] **Step 2: Spot-check env var names used in runbooks against codebase**

```pwsh
# Names used in runbooks
Select-String "Cosmos__AccountEndpoint|Opdb__ApiToken|ConnectionStrings__cosmos|AzureAd__|Foundry__|AiSearch__" docs/runbooks/ -Recurse | Select-Object Filename, Line

# Names actually used in the codebase
Select-String "Cosmos__AccountEndpoint|Opdb__ApiToken|ConnectionStrings__cosmos|AzureAd__|Foundry__|AiSearch__" src/ -Recurse -Include "*.cs","appsettings*.json" | Select-Object -First 15
```

Note any mismatches. Fix runbook references to match actual env var names.

- [ ] **Step 3: Check Azure CLI commands in runbooks use az stack**

```pwsh
Select-String "az deployment\b" docs/runbooks/ -Recurse
```

If any `az deployment` commands appear (old style), replace with `az stack group create` / `az stack sub create` per the Deployment Stacks invariant.

- [ ] **Step 4: Read each runbook (01–06 + h-chain)**

For each file check:
- Orientation sentence at top of each file (add if missing; one sentence stating what this runbook covers and when to use it)
- CLI commands match current `dotnet run --project src/PinballWizard.Cli -- <flags>` form
- No "TBD" stubs or unfilled sections
- Dates/versions are not hardcoded to values that will immediately become stale

- [ ] **Step 5: Apply all fixes across all 8 files**

- [ ] **Step 6: Commit**

```pwsh
git add docs/runbooks/
git commit -m "docs(audit) pass3: fix runbooks — env vars, CLI commands, orientation sentences, Deployment Stacks"
```

---

### Task 10: Fix docs/local-development.md and docs/observability.md

**Files:**
- Modify: `docs/local-development.md`
- Modify: `docs/observability.md`

- [ ] **Step 1: Read docs/local-development.md in full**

Check:
- Aspire version references — update any to 13.4.6
- CLI flags — verify against current `--help` output:

```pwsh
dotnet run --project src/PinballWizard.Cli -- --help
```

Compare the flag table in local-development.md against the `--help` output. Add any flags that are present in `--help` but missing from the doc; remove any flags that no longer exist.

- Env var names for Cosmos, OPDB, Storage — match codebase
- `start-apphost.ps1` steps still accurate
- Orientation sentence at top

- [ ] **Step 2: Apply fixes to docs/local-development.md**

- [ ] **Step 3: Get current OTel instrument registrations from codebase**

```pwsh
Select-String "CreateHistogram|CreateCounter|CreateGauge|AddMeter\b" src/ -Recurse -Include "*.cs" |
    Select-Object Line |
    Sort-Object Line -Unique |
    Select-Object -First 40
```

Compare against the instrument catalogue in `docs/observability.md`. Note any instruments in the catalogue that no longer exist in code, and any in code that are not in the catalogue.

- [ ] **Step 4: Read docs/observability.md in full**

Check against Step 3 output:
- Remove any catalogue entries for instruments that no longer exist in code
- Add entries for instruments in code that are missing from the catalogue (use the naming convention already in the doc)
- Orientation sentence at top

- [ ] **Step 5: Apply fixes to docs/observability.md**

- [ ] **Step 6: Commit**

```pwsh
git add docs/local-development.md docs/observability.md
git commit -m "docs(audit) pass3: fix local-development and observability — Aspire version, CLI flags, instrument catalogue"
```

---

### Task 11: Fix docs/operations.md, docs/decision-log.md, docs/cloudflare-setup.md

**Files:**
- Modify: `docs/operations.md`
- Modify: `docs/decision-log.md`
- Modify: `docs/cloudflare-setup.md`

- [ ] **Step 1: Read docs/operations.md in full**

Check:
- Azure resource names match current subscription (subscription `b1f33f17`, suffix `buutj`)
- Any references to the old subscription `4dce9fdd` (deleted) → remove
- Orientation sentence at top
- No "TBD" stubs

- [ ] **Step 2: Apply fixes to docs/operations.md**

- [ ] **Step 3: Read docs/decision-log.md in full**

Check:
- All entries follow the format: `## YYYY-MM-DD — [Short title]` with Decision / Alternatives considered / Rationale / Revisit when / Related
- Any entry whose "Revisit when" condition has been met — add a "Superseded by" note
- Orientation sentence at top

- [ ] **Step 4: Apply fixes to docs/decision-log.md**

- [ ] **Step 5: Check cloudflare-setup.md for Terraform references**

```pwsh
Select-String "terraform\b|\.tf\b" docs/cloudflare-setup.md -CaseSensitive
```

Any `terraform` command references must be replaced with `tofu` (per ADR-0028, the IaC tool is OpenTofu, not Terraform):
- `terraform init` → `tofu init`
- `terraform plan` → `tofu plan`
- `terraform apply` → `tofu apply`
- `HashiCorp Terraform` → `OpenTofu`

- [ ] **Step 6: Read docs/cloudflare-setup.md in full**

Check beyond the Terraform references:
- Cloudflare plan accurate (Pro)
- Features described (WAF, Bot Fight, DDoS, CDN) still match what's enabled
- Zero Trust Access / OTP gate description current
- Orientation sentence at top
- No "TBD" stubs

- [ ] **Step 7: Apply fixes to docs/cloudflare-setup.md**

- [ ] **Step 8: Commit**

```pwsh
git add docs/operations.md docs/decision-log.md docs/cloudflare-setup.md
git commit -m "docs(audit) pass3: fix operations, decision-log, cloudflare-setup — stale subscription, Terraform→OpenTofu"
```

---

### Task 12: Final verification and create PR

**Files:** None — verification and PR creation only.

- [ ] **Step 1: Verify no broken internal links in key docs**

```pwsh
# Check README internal links resolve
Select-String '\]\(docs/[^)]+\)' README.md |
    ForEach-Object {
        $path = [regex]::Match($_.Line, '\]\(([^)]+)\)').Groups[1].Value
        if ($path -notmatch '^http' -and !(Test-Path $path)) {
            Write-Host "BROKEN: $path"
        }
    }
```

Fix any broken links found.

- [ ] **Step 2: Verify no "TBD" stubs remain in Tier 1 docs**

```pwsh
Select-String "\bTBD\b|\bto be created\b|\bto be written\b" README.md,docs/vision.md,docs/guardrails.md,docs/quality-spec.md,CLAUDE.md -CaseSensitive
```

Fix any hits found.

- [ ] **Step 3: Verify every Tier 1 doc has an orientation sentence**

Manually confirm the first non-blank content of each Tier 1 doc is a clear 1–2 sentence orientation. Docs to check: README.md (already has it), docs/vision.md, docs/guardrails.md, docs/build-spec.md, docs/quality-spec.md, docs/architecture-v2.md, CLAUDE.md, .claude/INVARIANTS.md, docs/ai-development-model.md, docs/learning-from-failure.md, CONTRIBUTING.md, SECURITY.md.

Add orientation where missing.

- [ ] **Step 4: Commit any final fixes**

```pwsh
git add -A
git commit -m "docs(audit) final: orientation sentences, broken link fixes"
```

Only commit if there are actual changes.

- [ ] **Step 5: View full commit log for this branch**

```pwsh
git log --oneline main..HEAD
```

Expected: ~10–14 commits covering all three passes.

- [ ] **Step 6: Push and create PR**

```pwsh
git push -u origin docs/audit
```

```pwsh
gh pr create --title "docs(audit) Tier 1 + Tier 2 documentation sweep — accuracy, formatting, showcase quality" --body "$(cat <<'EOF'
## Summary

Three-pass documentation audit against the design spec at \`docs/superpowers/specs/2026-06-28-documentation-audit-design.md\`.

**Pass 1 — Showcase-landing (README, vision.md, adr/README, CONTRIBUTING, SECURITY):**
- README: ADR count 27→47, Aspire badge 13.2→13.4.6, Agents 1.4.0→1.5.0, admin complete (removed "placeholder" limitation), broken \`data-model.md\` link removed, Silverball Labs + shared component library added to capabilities, test count updated
- vision.md: mutation testing claim verified/corrected
- ADR index: confirmed all 47 entries present

**Pass 2 — Spec docs (guardrails, build-spec, quality-spec, architecture-v2, CLAUDE.md, INVARIANTS, PR-AUDIT, ai-development-model, learning-from-failure):**
- guardrails.md: stale \`c:\projects\` path fixed, self-audit description updated to /local-review + /standards-audit two-step, risk register dates updated
- build-spec.md: Phase 5 admin completeness noted, Phase 6 current state verified
- quality-spec.md: gate statuses and tool versions updated
- CLAUDE.md: Agents version 1.5.0, shared component table verified against codebase

**Pass 3 — Operational (runbooks, local-development, observability, operations, decision-log, cloudflare-setup):**
- Runbooks: env var names verified, CLI flags current, orientation sentences added
- observability.md: instrument catalogue reconciled against code
- cloudflare-setup.md: Terraform→OpenTofu throughout

## /local-review outcome

_Paste outcome here before merge._

## Test plan

- [ ] README renders on GitHub without broken badge or table formatting
- [ ] All internal links in README resolve (Task 12 Step 1 verification passed)
- [ ] No "TBD" stubs in any Tier 1 doc (Task 12 Step 2 verification passed)
- [ ] ADR index lists 47 entries
- [ ] Admin pages no longer described as placeholders in README
EOF
)"
```

- [ ] **Step 7: Add claude-code label**

```pwsh
$prNum = gh pr view --json number --jq .number
gh pr edit $prNum --add-label "claude-code"
gh pr view $prNum --json labels --jq '[.labels[].name]'
```

Expected: `["claude-code"]` in output.
