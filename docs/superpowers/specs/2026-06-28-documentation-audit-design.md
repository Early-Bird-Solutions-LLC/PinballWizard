# Documentation Audit Design

**Date:** 2026-06-28  
**Scope:** Tier 1 (showcase-facing) + Tier 2 (operational) docs  
**Branch:** `docs/audit` (separate from `feat/wif-showcase`)

## Goal

Every Tier 1 and Tier 2 doc is accurate, consistently formatted, and holds up under
prospect and senior-engineer scrutiny. No stale numbers, no placeholder stubs, no
broken links, no aspirational prose for shipped features.

## Formatting standard

**Structure**
- H1 = document title only (one per doc)
- H2 = major sections, H3 = subsections — no skipping levels
- Tables for any list where two or more columns add meaning; plain lists otherwise
- Fenced code blocks with language tags (`pwsh`, `bash`, `json`, `text`, `mermaid`)

**Prose**
- Present tense for what exists now; future tense only for genuinely unshipped features
- No "we will" or "will be" for shipped things
- Numbers spelled out under 10; digits for 10+
- Oxford comma

**Links**
- All internal cross-references use relative paths (e.g., `docs/adr/0012-...`)
- No bare URLs in prose — always `[descriptive text](url)`
- Broken links flagged and corrected or removed

**Admonitions**
- `> ⚠️` for warnings with operational consequence
- Plain blockquote for informational notes
- Consistent style within each doc

**Tables**
- Header row + separator on every table
- Left-align all columns

**Showcase-specific rules**
- Every doc opens with 1–2 sentences telling the reader what it is and who reads it
- No orphaned stubs — "TBD", "to be created", placeholder sentences get filled or removed

## Done criteria

A doc is done when:
1. All factual claims match the current codebase and live system state
2. All cross-references and links resolve
3. No future-tense prose for shipped features
4. No placeholder stubs
5. Formatting standard applied throughout
6. Opening orientation sentence present

## Three-pass execution plan

### Pass 1 — Showcase-landing (what a prospect reads in 5 minutes)

| Doc | Key accuracy checks |
|---|---|
| `README.md` | ADR count (27→47); Aspire version (13.2→13.4.6); admin pages (placeholder→complete); test count; broken `data-model.md` link; "What this demonstrates" missing Silverball Labs + Kineticist + shared component library; phase status table; docs map ADR range |
| `docs/vision.md` | Mutation testing claim; future-tense discipline; "passport features" framing |
| `docs/adr/README.md` | Confirm all 47 ADRs indexed |
| `CONTRIBUTING.md` | Accuracy pass |
| `SECURITY.md` | Accuracy pass |

### Pass 2 — Spec docs (what a senior reviewer reads to evaluate rigor)

| Doc | Key accuracy checks |
|---|---|
| `docs/guardrails.md` | Old path `c:\projects\CLAUDE.md`; "7-item" → 12-item self-audit; risk R12 status; risk register dates |
| `docs/build-spec.md` | Phase statuses; Phase 5 admin description; Phase 6 current state |
| `docs/quality-spec.md` | Gate statuses; any planned-but-shipped features |
| `docs/architecture-v2.md` | Forward-direction framing; no overclaims |
| `CLAUDE.md` | Aspire version; ADR range; admin capabilities |
| `.claude/INVARIANTS.md` | Invariants match code reality |
| `.claude/PR-AUDIT.md` | Item count correct; no stale items |
| `docs/ai-development-model.md` | Accuracy pass |
| `docs/learning-from-failure.md` | Accuracy pass |

### Pass 3 — Operational docs (what an operator reads)

| Doc | Key accuracy checks |
|---|---|
| `docs/runbooks/README.md` + 01–06 + h-chain | Commands match current CLI flags; env var names current |
| `docs/local-development.md` | Aspire version; CLI flags; env var names |
| `docs/observability.md` | Instrument names; additions/removals since written |
| `docs/operations.md` | Current operational state |
| `docs/decision-log.md` | Superseded decisions recorded |
| `docs/cloudflare-setup.md` | OpenTofu references; current setup state |

## Out of scope

- `docs/superpowers/specs/` and `docs/superpowers/plans/` — working artifacts, not showcase docs
- `docs/BRAINSTORM-HANDOFF.md`, `docs/PHASE5-DRIFT-AUDIT.md`, and other internal notes
- UI prototypes and screen specs under `docs/ui/`
- `.claude/` command and skill files (operational tooling, not documentation)
