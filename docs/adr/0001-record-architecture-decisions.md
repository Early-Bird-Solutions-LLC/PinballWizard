# 0001 — Record architecture decisions in this repository

**Status:** Accepted
**Date:** 2026-05-02

## Context

PinballWizard is a hobby project that doubles as a public portfolio
piece. As of this writing, the codebase has accumulated a number of
significant architectural decisions — the deterministic document ID
scheme, the choice of Playwright over Puppeteer-Sharp, the Clean
Architecture pivot, the locked Phase 2 platform shape (ACA + AI Search
Basic + Cosmos Serverless + Cloudflare Pro + MudBlazor + Entra External
ID), the Cosmos-data-driven ingestion sources whitelist — but those
decisions live scattered across the README, `CLAUDE.md`, `docs/`
markdown files, and project memory files.

A reviewer arriving at this repository for the first time has no single
place to look for "why is the project shaped the way it is?" That's a
gap. A second-time reader wants to understand whether a decision is
still load-bearing or has been superseded; today there's no such record.

[ENGINEERING_STANDARDS.md §10.2](../ENGINEERING_STANDARDS.md) already
calls for ADRs in [Nygard's format](https://www.cognitect.com/blog/2011/11/15/documenting-architecture-decisions),
listed in `docs/adr/`. This ADR makes that real.

## Decision

We record significant architectural decisions as Architecture Decision
Records in `docs/adr/`, following the Nygard format adapted for
markdown:

- 4-digit sequential prefix (`0001-…`, `0002-…`)
- Title in the filename and the `# 0NNN — Title` H1
- A `**Status:**` line (`Proposed` / `Accepted` / `Superseded by NNNN` /
  `Deprecated`)
- A `**Date:**` line in ISO 8601 format
- Sections: `## Context` / `## Decision` / `## Consequences`
- Optional sections: `## Alternatives considered` / `## References`

ADRs are **immutable once accepted**. Subsequent decisions that change
direction are recorded as new ADRs that supersede the old one; the
superseded ADR's status is updated to point at the new ADR but its body
is not rewritten.

The `docs/adr/README.md` index is the canonical list and is kept in
sync in the same PR that adds or changes any ADR.

## Consequences

**Positive:**
- A reviewer has one place to look for the "why" behind the project
  shape.
- Decisions become discussable as discrete artifacts. PR review can
  reject a code change that violates an existing ADR without arguing
  the underlying decision in the PR comments.
- The decision history is preserved through the supersession mechanism
  even when implementations change.

**Negative:**
- Adds a small amount of overhead to architectural changes — every
  significant decision now requires an ADR PR (or an ADR-touch in the
  same PR that implements the decision).
- Risk that ADRs become a paperwork ritual rather than a thinking tool
  if used for trivial decisions. Mitigation: bias toward "I'd want a
  reviewer to know this in two years" as the threshold.

## What qualifies as "significant"

Use the ADR format when the decision:

- Constrains future code (e.g., "all UI uses MudBlazor, full stop")
- Cannot be cheaply reversed (e.g., "Cosmos partitions by manufacturer")
- Has been deliberately considered against rejected alternatives
- A reviewer would want to understand without reading commit history

Skip the ADR format for routine work — adding a feature, fixing a bug,
refactoring within an established pattern. Use commit messages and
changelog entries for those.
