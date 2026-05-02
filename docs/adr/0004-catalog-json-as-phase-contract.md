# 0004 — `catalog.json` is the Phase 1 ↔ Phase 2 contract

**Status:** Accepted
**Date:** 2026-05-02 (codifies a decision implemented at project inception)

## Context

This project is split into two phases:

- **Phase 1** (this codebase, mostly complete): a scraper that produces
  `catalog.json`, `games.json`, and downloaded files.
- **Phase 2** (planned): a RAG pipeline that consumes Phase 1's output
  to provide search and Q&A with source citations. Documented in
  [`docs/infra_analysis.md`](../infra_analysis.md).

Phase 2 will be implemented later (and may end up in a separate
deployment unit even within the same repository — see ADR 0006). The
two phases need a contract: a stable, well-defined interface that lets
each phase evolve independently as long as the contract is honored.

## Decision

`catalog.json` (the master catalog of `DocumentRecord` entries with full
provenance) is the **API boundary** between Phase 1 and Phase 2.

The contract specifies:

- The JSON schema of a `DocumentRecord` — its `source.*`, `game.*`,
  `classification.*`, `timeline.*`, `http.*`, and `cross_references[]`
  fields, with their meaning and value ranges.
- The ID scheme (deterministic, see ADR 0002).
- The atomic-write guarantee — `catalog.json` on disk is always either
  the prior valid version or a new fully-valid version, never partial.
- The ordering and stability guarantees — entries are sorted by ID,
  field order is stable across runs.

Phase 2 reads `catalog.json` and joins to it via `document_id`. Phase 2
does not depend on any Phase 1 implementation detail beyond the
contract.

`games.json` (the structured game metadata: editions, MSRPs,
descriptions, images) is a peer artifact under the same contract.

The contract is **versioned**. Schema changes that break consumers will
ship a new top-level `schemaVersion` field and bump the major version.
Additive changes that don't break consumers ship as minor-version
updates.

## Consequences

**Positive:**
- Phase 1 and Phase 2 are decoupled. Phase 1 can refactor freely as
  long as the catalog output remains contract-compliant.
- A future Phase 2 implementer (us, in months) doesn't need to read
  Phase 1 source to integrate — the contract is sufficient.
- Test data for Phase 2 can be a captured `catalog.json` from a Phase 1
  run, no live scraper required.
- The catalog itself becomes the public deliverable for any
  third-party who wants to consume Stern documentation in structured
  form.

**Negative:**
- Schema discipline is non-negotiable. Once Phase 2 is consuming
  `catalog.json`, breaking changes require a coordinated migration.
- Adds an artifact-design step (the JSON schema) on top of regular
  domain-model work. We pay this cost in exchange for the decoupling.

## Practical guidelines

- A change to the `DocumentRecord` shape requires updating tests that
  verify catalog-write round-trips against the contract.
- When Phase 2 lands, the contract test moves from "catalog round-trip"
  to "catalog round-trip + Phase 2 ingestion smoke test."
- Adding fields is safe (additive). Renaming or removing fields is a
  breaking change and must bump `schemaVersion`.
- The contract is defined by the production C# types in
  `PinballWizard.Core/` (the source of truth) plus a JSON Schema
  derivation in `docs/data-model.md` (the human-readable form). When
  these diverge, the C# wins; the markdown is updated in the same PR.

## References

- [`docs/scraper_plan_v4.md`](../scraper_plan_v4.md) — the original
  data-model description that informed this decision.
- [`docs/infra_analysis.md`](../infra_analysis.md) — Phase 2's planned
  consumption pattern.
