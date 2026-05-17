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

---

## Amendment 1 — OPDB group tier and canonical-base fold (2026-05-17)

**Status:** Accepted (additive — does not reverse the original decision).
**Driver:** [docs/plans/opdb-group-tier-modeling.md](../plans/opdb-group-tier-modeling.md),
Option A. Live OPDB + Cosmos verification.

### What this amends and what it does NOT

This amendment refines *which OPDB record is the canonical `Machine`*
when OPDB splits one physical machine across several `is_machine` base
records. It does **not** change:

- The union/membership stance — OPDB still owns the catalog spine; this
  is about resolving *within* OPDB's own data, not admitting non-OPDB
  machines (CLAUDE.md invariant #8 unchanged).
- The spine key — `Machine.Id` remains the 2-segment OPDB base ID. No
  document-`id` migration.
- The scraper reconciliation flow above — unchanged.

### Correction to a falsified assumption

The original **Negative** consequence "OPDB is comprehensive for
production-scale pinball; machines OPDB doesn't know about are edge
cases" was tested against the licensed-IP titles (Foo Fighters,
Stranger Things, AC/DC, Metallica, Rush, The Beatles, Stern Godzilla
2021) and found **OPDB has all of them**. The earlier eval "floor" was
never an OPDB-coverage gap — it was three local defects (D1–D3 in the
plan). The comprehensiveness assumption stands; what was wrong was our
*modeling* of OPDB's three-tier structure.

### OPDB's three-tier reality

```text
{group}                  is_machine_group   clean title ("Godzilla")   — NOT in /api/export
 ├─ {group}-{base}        is_machine pm:1    "Godzilla (Pro)"           — canonical hardware
 └─ {group}-{base}        is_machine pm:0    "Godzilla (Premium/LE)"    — edition-grouping record
      └─ {group}-{base}-{alias}  is_alias    "(Premium)" / "(LE)" / …   — edition aliases
```

`physical_machine` (`pm`) is **not** uniformly applied:
Godzilla/Foo Fighters use `pm:1` + `pm:0`; Metallica is 3× `pm:1`;
Beatles is one `pm:1` named "(Gold)" + aliases. The fold below
normalizes all three shapes.

### Decision (Amendment 1)

For each OPDB **group** (records sharing the leading ID segment, e.g.
`GweeP`):

1. **Canonical row** = the `is_machine` base record with `pm:1` and,
   among ties, the lexicographically lowest OPDB ID. Its `Machine.Id`
   is the group's catalog identity.
2. **`Title`** = the `is_machine_group` record's clean name (fetched per
   unique segment via `GET /api/machines/{segment}`, cached per sync
   run; that record is absent from `/api/export`). This fixes the
   empty-`common_name` → edition-suffixed-title defect.
3. **`Editions[]`** = the union of (a) every alias under every base
   record in the group and (b) each *non-canonical* base record itself,
   each mapped to a `MachineEdition` retaining its own
   Msrp/availability/features. Editions remain **first-class and
   individually distinguishable** — Pro/Premium/Collector's are
   different games with different rules and cost; the fold consolidates
   them onto one resolvable machine, it does not flatten the
   distinction.
4. **`GroupId`** (new field) = the group segment, set on all related
   rows.
5. **Non-canonical base records** are retained but flagged
   `IsEditionGroupingRecord = true` (new field) and excluded from
   title-lookup so a title resolves to exactly the canonical row.

Expected group cardinality: typically ~3 editions, at most ~10 — the
fold is a small bounded in-memory operation.

### Field-ownership precedence (resolves a conflict with the table above)

The original field-ownership table marks `Editions` **Scraper-owned,
replaced wholesale on reconcile**. The OPDB fold now *also* writes
`Editions[]`. Precedence is explicit:

- **OPDB sync** establishes the *baseline* `Editions[]` from the group
  fold (every edition exists, with OPDB-derived names + source URLs).
- **Scraper reconciliation** still replaces `Editions[]` wholesale when
  it has a slug/title match — manufacturer pricing/availability is
  intentionally fresher (the original rationale stands). The scraper
  reconciler MUST preserve any edition the OPDB fold knew about that the
  scraper page omits (merge-by-edition-name, OPDB-known editions are not
  dropped) so a sparse manufacturer page cannot erase an edition the
  catalog legitimately has. `Title`, `GroupId`, `IsEditionGroupingRecord`
  remain OPDB-owned and are never touched by the reconciler.

This keeps "scraper data is the freshest pricing truth" intact while
guaranteeing the fold's completeness invariant survives a thin scraper
run.

### Consequences (Amendment 1)

- Positive: a generic question ("tell me about Stern Godzilla") resolves
  to one machine whose `Editions[]` is the complete distinct set — the
  `getMachineByTitle` contract already returns that shape, so the
  user-facing surface becomes *correct* without a tool/citation change.
- Negative: OPDB sync gains a per-group metadata fetch (bounded, cached)
  and a cross-base fold pass; `OpdbMachineDto` gains `physical_machine`;
  `Machine` gains `GroupId` + `IsEditionGroupingRecord`. The
  reconciler's wholesale-replace gains a merge-preserve rule (above).
- Neutral: existing single-edition machines are unaffected (no group →
  no fold; the negative-fixture test "Indiana Jones (The Pinball
  Adventure)" guards against spurious folding).
