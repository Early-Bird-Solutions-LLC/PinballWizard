# 0011 — Manufacturer scraper data reconciles INTO OPDB-keyed Machines

**Status:** Accepted
**Date:** 2026-05-02

## Context

The catalog has two ingestion paths producing structured data about
the same physical pinball machines:

1. **OPDB sync** (existing) — pulls the canonical OPDB record set,
   creates/updates `Machine` aggregates partitioned by manufacturer
   key, populates `Title` / `Year` / `Designers` / `Themes`. Leaves
   `Editions` empty and `ManufacturerSlugs` empty (OPDB does not
   publish the manufacturer-site slug for a given OPDB ID).

2. **Per-manufacturer scrapers** (existing) — Stern / JJP / AP /
   Spooky each produce a `GameRecord` per machine they discover, with
   the manufacturer-site slug, the canonical page URL, and (when the
   site exposes it) edition-level data: name, MSRP, availability,
   description, unique features. `GameRecord` lives in
   `PinballWizard.Core.Models` as a legacy/working-set type and is
   serialized to `data/metadata/games.json`.

Phase 2 needs a single source-of-truth for machine data — the
OPDB-keyed `Machine` aggregate. Today, OPDB owns the catalog spine
but is missing edition-level marketing data; the scrapers own
edition-level data but lack a stable cross-manufacturer ID. The bug
this creates: a Phase 2 RAG query asking "what are the editions of
Beetlejuice?" cannot be answered because the data is in two places
that aren't joined.

The architectural debt was called out as "deferred to follow-up" in
the OPDB / JJP / AP / Spooky PR descriptions (#30 / #31 / #32 / #33).
Four PRs deep is enough.

## Decision

**Scraper data reconciles INTO existing OPDB-keyed `Machine`
aggregates.** OPDB owns the catalog spine; scrapers contribute
edition data and populate the `ManufacturerSlugs[manufacturerKey]`
back-reference.

### Match strategy (two-pass)

1. **Slug match (fast path).** Look up Machines for the manufacturer
   partition; find the one whose
   `ManufacturerSlugs[manufacturerKey] == GameRecord.Slug`. Constant
   per-record work after the first sync seeds the slug map.

2. **Title-normalize fallback (bootstrap path).** First time a
   manufacturer's scraper runs, every Machine in the partition has
   an empty `ManufacturerSlugs` map (OPDB sync filled the rest of
   the record but not the slug). Match the GameRecord by normalized
   title — lowercase, strip non-alphanumeric. If exactly one Machine
   matches, populate `ManufacturerSlugs[mfg] = slug` and treat as a
   match. If zero or multiple match, log warning and skip.

The fallback handles the bootstrap case automatically; subsequent
runs use the fast path.

### Field ownership

| Field | Owner | Behavior on reconcile |
| --- | --- | --- |
| `Id` (OPDB ID) | OPDB | Never changed by reconciler |
| `PartitionKey` (manufacturer key) | OPDB | Never changed |
| `ManufacturerDisplayName` | OPDB | Never changed |
| `Title` | OPDB | Never changed |
| `Year` | OPDB | Never changed |
| `Designers` | OPDB | Never changed |
| `Themes` | OPDB | Never changed |
| `Editions` | **Scraper** | Replaced wholesale on reconcile (scraper data is current pricing/availability) |
| `ManufacturerSlugs[mfg]` | **Scraper** | Set / replaced on reconcile |
| `OpdbSourceUrl` | OPDB | Never changed |
| `FirstSeenAt` | OPDB | Never changed |
| `LastSeenAt` | Either | Updated to now on reconcile |

A scraped game with no matching Machine is logged as
`unmatched_games` in the result and skipped — never written. This
keeps the spine clean: OPDB is the gate for what counts as a real
machine. Manufacturers that ship machines OPDB doesn't yet know
about will appear in the warning logs and require an OPDB
contribution to be reconciled.

### Manufacturer key derivation

The `GameRecord.GameId` prefix encodes the manufacturer:

| GameId prefix | Manufacturer key |
| --- | --- |
| `game_jjp_*` | `jjp` |
| `game_ap_*` | `americanpinball` |
| `game_spooky_*` | `spooky` |
| `game_*` (no further prefix) | `stern` (default — Stern was the original scraper) |

The keys match `OpdbMachineMapper.NormalizeManufacturerKey` exactly
so a JJP `GameRecord` lands in the same partition the OPDB sync
wrote JJP machines to.

### Where the reconciler runs

The reconciler is exposed as `IScraperReconciliationService` in
`PinballWizard.Application/Sync/`. It does not auto-run from the
local CLI. Per the parallel execution plan, in production it will
be invoked from a `scraper-mfg-sync` ACA Job that runs after the
per-manufacturer scrapers complete. CLI integration is deferred
until Cosmos infra is deployed.

## Consequences

**Positive:**
- Single source of truth: every Phase 2 query joins through `Machine`.
- The reconciler is idempotent — re-running against the same scraper
  data produces no writes after the first run.
- Provenance is preserved: `ManufacturerSlugs[mfg]` records *which*
  manufacturer-site slug pointed at this Machine, so the RAG
  citation chain (Machine → manufacturer page → catalog document)
  remains intact.
- The two-pass match strategy bootstraps automatically — no manual
  slug-map seeding step.

**Negative:**
- Machines OPDB doesn't know about cannot be reconciled. For a
  hobby-scope Phase 1, this is acceptable: OPDB is comprehensive
  for production-scale pinball. Edge cases (boutique runs, prototype
  reveals) get logged for manual triage.
- Title-normalize fallback is a fuzzy match. Risk: two OPDB records
  with the same normalized title (different years, different OEMs)
  in the same manufacturer partition would log as "ambiguous" and
  skip. Mitigation: log includes both candidate Machine IDs so the
  operator can fix ambiguity manually.
- `games.json` (the legacy catalog) remains alongside the Cosmos
  data for now; sunsetting it is its own decision (out of scope).

## Alternatives considered

- **Scraper-direct insert** — scrapers create Machine documents
  themselves, no OPDB join. Rejected: produces orphan Machines that
  don't tie into OPDB, breaks the catalog spine.
- **Title-only match** — drop slug fast path. Rejected: every
  reconciliation run becomes O(N) per record; fast path is essential
  at scale.
- **OPDB-first, scrapers-only-fill-blanks** — scrapers update only
  fields where the existing Machine value is null/empty. Rejected:
  pricing and availability are intentionally fresher on the
  manufacturer site than on OPDB; treating "scraper as second
  citizen" loses that freshness.
