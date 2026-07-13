# Design — Unified Machine Resolution

**Date:** 2026-07-13
**Status:** Approved (design); implementation not started
**ADR:** [ADR-0054](../../adr/0054-unified-machine-resolution.md)
**Issues:** #745 (ingestion gaps), #758 (learning-from-failure), #762 (upsert semantics), #749 (coverage gap)
**Supersedes in part:** the slug-only join introduced alongside ADR-0004 provenance

## Problem

Six subsystems independently match free text / slugs / titles to `Machine` records, each with
its own normalizer. They disagree with one another, and the primary one — the document→machine
linker — joins through a field that is populated for 4% of the catalog.

### The structural defect

Every `DocumentLinker` tier resolves through `Machine.ManufacturerSlugs`:

| Tier | Signal | Joins against |
| --- | --- | --- |
| 1 | `raw.Game.Slug` | slug index |
| 2 | filename | slug index (word-boundary) |
| 3/4 | page-1 / page-2 text | slug index (word-boundary) |

`ManufacturerSlugs` is written only by `ScraperReconciliationService` when a **game-page scraper
discovers the game**. The linker's own log states the consequence:

```text
DocumentLinker: indexed 1683 machine slugs across 86 machines (of 2162 total).
```

**96% of the catalog is structurally unlinkable.** The join key is an incidental artifact of
scraper coverage rather than the machine's canonical identity (OPDB title + manufacturer + year +
group), which is authoritative and complete for all 2162 machines.

The PR #595 title fallback indexes the **full** title (`houdini master of mystery`), a string real
filenames never contain, so it rarely fires.

### How this surfaced

American Pinball: 6 machines, **all with `manufacturer_slugs = None`**, and a sitemap exposing only
2 of 6 game pages. No tier can bind any AP document. PR #752 attempted to fix AP by deriving a
document-side slug — provably futile, since there is nothing on the machine side to match against.
It was reverted (#757); the full failure chain is #758.

### Divergent normalizers

| Subsystem | Normalizer | Spaces | `&` |
| --- | --- | --- | --- |
| DocumentLinker | `LinkingUtilities.NormalizeForMatch` | kept (token delimiters) | → space |
| ScraperReconciliationService | `NormalizeTitle` / `NormalizeFranchiseTitle` | **stripped** | stripped |
| MachineTitleLookup / OpdbSync | `MachineTitleLookup.NormalizeTitle` | kept | **kept as `&`** |
| MachineGroundingTool | `TokenizeForOverlap` | token set | split char |
| TiltForumsGameMatcher | `NormalizeTitleTokens` | token set (diacritic-folded) | split char |

The `&`/`and` retry loop in `MachineGroundingTool` (L217–228) exists **solely to bridge two of our
own normalizers**. That is the tell.

## Goal

One resolution core, one normalizer, canonical-identity-first matching with curated aliases as
first-class data — adopted by all six consumers, each behind its own regression gate.

## Non-goals

- #760 (RAG ingestion politeness fallback) — separate fix.
- AP game-page discovery beyond the sitemap's 2 games — aliases make it unnecessary for linking.
- OCR Tier 5.
- Any engagement/per-user surface on the admin queue (ADR-0027 posture).

## Architecture

New Application-layer namespace `PinballWizard.Application.Resolution`. Three components:

### `MachineTextNormalizer`

The single normalizer. Ordered transformations:

1. Diacritic-fold (decompose, strip combining marks) — from `TiltForumsGameMatcher`
2. Insert token boundaries: camelCase, letter→digit, digit→letter, acronym→lower — from
   `LinkingUtilities.InsertTokenBoundaries`
3. Lowercase (invariant)
4. Strip apostrophes (`don't` → `dont`)
5. Fold `&` → `and` (**resolves the divergence**; the grounding tool's `&`/`and` retry loop is deleted)
6. All other separators/punctuation → space; collapse runs; trim
7. Split on space → `IReadOnlyList<string>` tokens

Canonical key = tokens joined by single space. Exposes `Normalize(string) → Tokens` and
`Key(string) → string`. The five existing normalizers are retired.

### `MachineIdentityVariants`

Pure function: `Machine` + curated aliases → the set of matchable **variants**, each carrying its
`VariantKind` (used by the confidence policy):

| VariantKind | Example (Houdini) | Source |
| --- | --- | --- |
| `FullTitle` | `houdini master of mystery` | `Machine.Title` |
| `FranchiseTitle` | `houdini` | title with subtitle separator (colon or dash) stripped and trailing qualifiers consumed |
| `TitleWithEdition` | `godzilla pro` | `Title` + `EditionTokens` |
| `ManufacturerPrefixed` | `stern godzilla` | manufacturer match-tokens + `Title` |
| `ScraperSlug` | `houdini` | `Machine.ManufacturerSlugs` (**demoted to one evidence source among several**) |
| `CuratedAlias` | `gtf`, `okto`, `hwl` | `machine_aliases.v1.json` |

Trailing-qualifier vocabulary (`edition`, `pinball`, `remake`, `gamekit`, `vaultedition`, …) is
owned here, generalizing PR #750's reconciler-only fix.

**This one generator feeds both stores** (below), so batch and interactive consumers see identical
variants — the divergence cannot re-open.

### `MachineResolver`

```csharp
Resolve(ResolutionQuery) -> ResolutionResult
```

`ResolutionQuery { Text | Tokens, ManufacturerHint?, EvidenceKind }`

`EvidenceKind ∈ { ProvenanceSlug, Filename, PageText, ScrapedTitle, FreeText }`

Policy pipeline (ordered, first decisive stage wins):

1. **Exact variant match** on the canonical key.
2. **Franchise-prefix + trailing-qualifier consumption** — catalog variant is a strict prefix of the
   query and the remainder is entirely trailing-qualifier tokens (generalizes #750).
3. **Token word-boundary containment** — query tokens contain a variant's token sequence;
   **longest variant wins** (today's Tier-2 rule, preserved).
4. **Manufacturer scoping** — **hard** filter for fuzzy evidence kinds (`Filename`, `PageText`);
   **soft** preference for provenance kinds (`ProvenanceSlug`, `ScrapedTitle`). This preserves the
   linker's current, deliberate `NarrowToSourceManufacturer` vs `PreferByManufacturer` split.
5. **Edition-family collapse** by `GroupId` (`IsEditionFamilyByGroup` — GroupId-only, matching
   both the linker and the reconciler today, issue #677).
6. **Confidence** → result.

`ResolutionResult`:

| Variant | Meaning |
| --- | --- |
| `Resolved(machine, evidence)` | single machine |
| `ResolvedFamily(groupId, machines, evidence)` | one edition family |
| `Ambiguous(candidates, evidence)` | multiple non-family candidates |
| `NoMatch` | nothing matched |

`evidence` records `{ EvidenceKind, MatchedVariant, VariantKind, Stage }` — for observability and
for the review queue.

#### The single-word guard, principled

Today: single-word titles are **excluded from the index entirely** (`normTitle.Contains(' ')`),
because "Pinball" (Stern, 1977) matched 172 documents.

New: a single-token variant is **eligible only for exact-match evidence** (stage 1), never for
containment stages (2/3). Same protection, but the machine remains resolvable when the evidence is
strong (an exact `ProvenanceSlug` of `pinball`, or a curated alias). The guard becomes a rule of
the policy, not a hole in the index.

## Two stores, one variant generator

| Consumer class | Store | Notes |
| --- | --- | --- |
| Batch (DocumentLinker, Reconciler, backfills) | In-memory index, built per run by streaming machines + seed | What the linker does today; now fed by `MachineIdentityVariants` |
| Interactive (GroundingTool, PB Freshdesk, Kineticist) | `machine_title_lookups` point-reads + AI Search fuzzy tier (ADR-0049) | `OpdbSyncService` population phases now call `MachineIdentityVariants` |

Because `OpdbSyncService` populates `machine_title_lookups` from the same variant generator,
**curated aliases become resolvable in the Wizard for free** — `getMachineByTitle("GTF")` works.

The curated seed also generates the AI Search **synonym map** (`machine_synonyms.v1.txt`), so there
is exactly one place a human writes "HWL means Hot Wheels."

## Alias seed — `data/seeds/machine_aliases.v1.json`

Follows the `community_resources.v1.json` pattern: loader with fail-fast validation,
`SeedPathResolver`, contract tests, PR review as the curation gate.

```json
{
  "version": 1,
  "aliases": [
    { "alias": "GTF", "opdbGroupId": "<real OPDB group id for Galactic Tank Force>",
      "manufacturerKey": "americanpinball",
      "notes": "AP support-page filename abbreviation (GTF-Quick-Reference-Guide.pdf)",
      "addedBy": "jkeeley2073" }
  ]
}
```

Every `opdbGroupId` MUST be looked up from the live catalog when the seed is authored — never
guessed. The loader's fail-fast validation (group must exist) enforces this, and the contract test
fails CI on a dangling reference. This is the #758 rule applied to seed data.

Rules:

- Keyed to **group** (`opdbGroupId`) by default so an alias resolves to the edition family;
  `machineId` permitted for edition-specific aliases.
- `manufacturerKey` **required** — an alias is always manufacturer-scoped (prevents `hw` colliding
  across manufacturers).
- Validation: non-empty alias; alias normalizes to ≥1 token; group/machine must exist at load
  (fail-fast, logged); no duplicate `(alias, manufacturerKey)` pairs.
- Machine-derived variants (scraper slugs, OPDB edition/manufacturer tokens) are **not** seeded —
  they flow automatically. The seed holds **human-curated** entries only.

Initial content (from the captured AP fixtures + verified catalog): `gtf`, `okto`, `hw`, `hwl`,
`lov`, `api houdini`. `Rampage` is **not** aliased — it has AP manuals but no OPDB machine, so it
correctly stays `not_in_catalog`.

## Ambiguity → `needs_review` + admin queue

New `link_status = needs_review`, persisted with the evidence:

```text
link_review {
  candidates: [ { machineId, machineTitle, score, evidenceKind, matchedVariant } ],
  createdAt
}
```

- The resolver **never auto-picks** among non-family candidates (provenance is sacred;
  mis-attribution is the worse failure).
- Admin page `/admin/link-review` (`AppDataGrid`, per ADR-0046) lists `needs_review` docs with
  their candidates and the matched evidence.
- Resolving a row writes a **`link_overrides`** record (Tier 0 — the mechanism already exists and
  has had no UI) and flips the doc to `pending` for re-link.
- Public surfaces treat `needs_review` exactly like `not_in_catalog` (invisible — never surface a
  document we cannot attribute).
- The admin scan is one cross-partition query on an admin-only path — allow-listed per ADR-0036,
  as the linker's existing pending-scan already is.
- Metered: `pinwiz.linking.needs_review_total` (tagged `manufacturer`, `evidence_kind`).

## `UpsertRawAsync` semantics (#762)

Today `CosmosRawDocumentRepository.UpsertRawAsync` preserves **everything** on an existing record
except `timeline.last_checked_at` and merged cross-references. The intent (protect linker state) is
right; the implementation also freezes **scraper-owned** fields, so **a scraper-code change can
never reach an already-stored document**. No unit test can reveal this; it only appears against a
populated live corpus. It is why #752 was doubly inert.

Split the record into two explicit blocks:

| Block | Fields | On re-scrape |
| --- | --- | --- |
| **Linker/admin-owned** | `machine_id`, `link_status`, `link_review`, `ManuallyLinked`, `PlatformGeneric`, `run_id` (write-once), `timeline.first_discovered_at`, `file.local_path` / blob state, `http.etag` / `last_modified` | **Preserved** |
| **Scraper-owned** | `source.*`, `game.*`, `classification.*`, `timeline.last_checked_at`, cross-references (merged) | **Refreshed** |

- If `game.slug` **or** `classification.document_type` changed, flip `link_status` → `pending`
  (unless `ManuallyLinked`, which always wins) so the doc is re-linked against the new evidence.
- Writes are **ETag-conditional** (`ItemRequestOptions.IfMatchEtag`) — scraper and linker can race
  on the same document (ADR-0025 lost-update protection).

Re-scrape becomes the legitimate refresh path. The #752 trap is removed structurally, not papered
over with a backfill verb.

## AP specifics (fold-in)

- **Classification** — `ClassifyDocumentType` learns AP's real patterns, derived from the 38
  captured URLs: `Quick-Reference-Guide` / `Service-Manual` / `Game-Manual` / `-Manual-` →
  `Manual`; install / fix / update instruction docs → `ServiceBulletin`. Existing AP docs are
  corrected by the existing `--reclassify-documents` verb. (Today they are `Other`, which RAG
  ingestion skips — so AP could never be indexed even once linked.)
- **Linking** — needs **no AP-specific code**. Franchise variants (`houdini`, `oktoberfest`,
  `hot wheels`, `galactic tank force`) plus curated aliases resolve the captured filenames through
  the normal pipeline. Generic docs (`Shaker.pdf`, `Assembly.pdf`, `Power-Distribution.pdf`)
  correctly `NoMatch` → `not_in_catalog`; an admin may Tier-0 them to `PlatformGeneric`.
- The `AmericanPinballBulletinPage` source type + `InferManufacturerKey` mapping — the **sound**
  half of #752 — returns with the DocumentLinker migration.

## Verification gates

Each consumer migrates behind its own gate. This is what makes parallel migration safe.

| Gate | Protects | Definition |
| --- | --- | --- |
| **Golden link set** | DocumentLinker | Captured from live: every currently-`linked` document (~373) → its expected `machine_id`. The new resolver must reproduce it. `linked → different machine` is 🔴 blocking. `linked → needs_review` is reviewable (report, don't auto-fail). `not_in_catalog → linked` is a **win**, reported. |
| **Reconciler parity snapshot** | ScraperReconciliationService | Machine→`ManufacturerSlugs` state + match-outcome counts (slug/title/group/unmatched/ambiguous) on fixtures must not regress. |
| **ADR-0049 eval** | MachineGroundingTool | Live Hit@1 95% / MRR 0.963 must hold. |
| **Existing suites + fixtures** | TiltForums, Kineticist, PB Freshdesk | Current tests, incl. nickname/category fixtures. |
| **Captured-fixture rule** (#758) | everything | Scraper/parsing fixtures MUST be captured from the live source (`tests/…/Fixtures/Ap/CAPTURE.md` pattern), never hand-authored. Promoted to a `.claude/standards` rule. |
| **Corpus-coverage probe** (#748) | end-to-end | Final ground truth. The `ap` source gap must close; #749 auto-closes. |

The golden set and parity snapshot are **captured read-only from live before any migration** — they
are the definition of "no regression."

## Error handling

- Resolver is pure and total — it returns `NoMatch`/`Ambiguous`, never throws on unmatched input.
- Seed load failure is **fail-fast at startup** (as `CommunityResourceLoader` is): a corrupt alias
  file must not silently degrade attribution.
- An alias referencing a non-existent group/machine is logged + skipped at load, and is a contract
  test failure in CI — it never silently mis-attributes.
- Ambiguity is **visible** (`needs_review` + metric), never a silent drop and never a guess —
  invariant #17.

## Testing

- **Normalizer golden tests** — a table of inputs → expected tokens, including every case that
  motivated the old normalizers (`&`/`and`, camelCase, digits, diacritics, apostrophes,
  `Hot-Wheels` vs `Hotwheels`).
- **Variant generator tests** — per `VariantKind`, incl. subtitle stripping
  (`Houdini: Master of Mystery` → `houdini`) and trailing qualifiers
  (`Medieval Madness Merlin Edition Pinball` → `medieval madness`).
- **Resolver policy tests** — one fixture per stage and per `EvidenceKind`; the single-word guard
  (a `pinball` containment query must NOT match the 1977 Stern machine; an exact `pinball`
  provenance slug MAY).
- **Ambiguity tests** — non-family multi-candidate → `Ambiguous`; same-group multi → `ResolvedFamily`.
- **Upsert tests** — a `Linked` + `ManuallyLinked` doc survives re-scrape; a changed slug flips
  `pending`; ETag conflict is handled.
- **Golden-set replay** — the regression gate above, run in CI against the captured snapshot.
- All scraper fixtures captured from live per the #758 rule.

## Work streams and parallelization

**Serial spine, parallel everything else.**

### S0 — Contract PR (serial, first, small)

`IMachineResolver`, `ResolutionQuery`/`ResolutionResult`/`EvidenceKind`/`VariantKind`,
`MachineTextNormalizer` + its golden tests, the alias seed **schema**, and ADR-0054. Freezes what
every other stream codes against. **Nothing else starts until this merges.**

### Wave 1 — parallel (after S0)

| Stream | Deliverable | Depends on |
| --- | --- | --- |
| **S1** | `MachineIdentityVariants` + in-memory index + `MachineResolver` policy | S0 |
| **S2** | #762 upsert semantics + ETag + re-link-on-change | S0 (independent of resolver) |
| **S3** | Golden-set capture + reconciler parity snapshot tooling (read-only vs live) | S0 |
| **S4** | AP classification rules + captured fixtures | S0 (independent) |
| **S5** | `machine_aliases.v1.json` + loader + contract tests + synonym-map generation | S0 |
| **S6** | `needs_review` status + `/admin/link-review` queue UI | S0 |

### Wave 2 — parallel consumer migrations (after S1 + S3)

One PR per consumer, each behind its gate:

| Stream | Consumer | Gate |
| --- | --- | --- |
| **M1** | `DocumentLinker` (+ restores #752's source-type half) | Golden link set |
| **M2** | `ScraperReconciliationService` (+ `--backfill-manufacturer-slugs`) | Reconciler parity snapshot |
| **M3** | `MachineGroundingTool` + `OpdbSyncService` variant population (deletes the `&`/`and` retry) | ADR-0049 eval |
| **M4** | `TiltForumsGameMatcher` | Existing fixtures |
| **M5** | `KineticistGameResolver` (legacy fallback path) | Existing fixtures |
| **M6** | PB Freshdesk category matcher | Existing fixtures |

M1 depends additionally on S5 (aliases) and S6 (`needs_review`) for full behavior; it can merge
with aliases empty and `needs_review` mapped to `not_in_catalog` if those lag, then tighten.

### Wave 3 — live verification (serial, operator-gated)

Each step requires explicit approval per the confirm-before-live-ingestion rule:

1. Re-scrape AP (`--source ap_bulletins`, `--source ap`) — now effective, because S2 lets the
   re-scrape refresh stored docs.
2. `--reclassify-documents` (AP `Other` → `Manual`/`ServiceBulletin`).
3. `--relink-all`.
4. `--download-documents` → `--run-rag-backfill` (blob-first; see #760 for why this must not fall
   back to source-site fetches).
5. **`--corpus-coverage`** — the `ap` gap must close; #749 auto-closes.

## Risks

| Risk | Mitigation |
| --- | --- |
| Shared linker logic touched for every manufacturer — highest blast radius in the repo | Golden link set captured from live *before* migration; `linked → different machine` is blocking |
| Broader matching → false-positive attribution (the "Pinball"/172-doc incident) | Single-word guard preserved as an evidence-kind rule; ambiguity → `needs_review`, never a guess |
| Six parallel migrations diverge again | One contract (S0) frozen first; one normalizer; one variant generator feeding both stores |
| Curated aliases become a dumping ground | Seed is human-curated only, manufacturer-scoped, PR-reviewed, contract-tested; machine-derived variants stay automatic |
| Design built on assumptions (the #752 failure mode) | Every claim here is verified against live data: the 86/2162 figure, the 6 AP machines with null slugs, the 38 captured AP filenames, the five normalizers |

## What proves this worked

The corpus-coverage probe (#748) reports **zero source gaps**, and the golden link set replays with
no `linked → different machine` regressions. Not "the tests pass" — #752's tests passed.
