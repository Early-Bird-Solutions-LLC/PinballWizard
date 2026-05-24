# Document Linking Design: Cloud-First, Content-Based Machine Resolution

**Date:** 2026-05-23
**Status:** Approved
**Branch:** feature/probe-unlinked-docs
**Author:** jkeeley2073

---

## Context

As of PR #271, 434 of 525 scraped documents are linked to machines via Pass 1 (cross-reference slug), Pass 2 (filename slug), and Pass 3 (ADI cover-page). 91 documents remain unlinked — 83 service bulletins and 8 legacy manuals.

Probe results from `data/eval/results/probe-unlinked-20260523T121905Z.json` show that content-based linking (page 1 text extraction with word-boundary matching) can resolve ~54 of the 91 remaining docs. The rest require page 2, ADI OCR, platform-generic classification, or games-catalog additions.

This spec also eliminates `catalog.json` as a production artifact. The scraper now writes directly to Cosmos; `catalog.json` and `games.json` are retired as system-of-record files.

---

## Goals

1. Definitively link the 91 unlinked documents using document content, not fuzzy heuristics.
2. Adopt a cloud-first architecture: Cosmos is the system of record end to end.
3. Support multi-machine documents correctly — a bulletin covering 4 games indexes under all 4.
4. Provide an admin UI for human-in-the-loop review of documents the linker cannot resolve automatically.
5. Make the system self-improving: manual admin decisions feed back as tier-0 hints for future similar documents.

---

## Decisions Made

| Decision | Choice | Rationale |
|---|---|---|
| Multi-machine Cosmos representation | N records, one per `(document_id, machine_id)` pair | Partition key stays `machine_id` — aligned with the RAG query access pattern. Each record is independently retrievable, embeddable, dead-letterable. |
| Unlinked state representation | Two containers: `scraped_documents_raw` + `scraped_documents` | Clean contract between scraper (writes raw), linker (bridges), and RAG pipeline (reads linked). No mixed partition key strategies in one container. |
| Linking trigger | Separate async linking pass (ACA Job / `--link-documents` CLI) | Single responsibility at every boundary. Scraper scrapes; linker links; pipeline embeds. Failures are retriable without re-scraping. |
| Feedback loop | Dedicated `link_overrides` container | Admin decisions are first-class, auditable, and revocable. Linker loads overrides as tier 0 at startup. |
| `catalog.json` / `games.json` | Retired as production artifacts | Cosmos is authoritative end to end. Files may be kept as optional local debug snapshots. |

---

## Data Model

### `scraped_documents_raw` container

**Partition key:** `document_id`
**Written by:** Scraper (on every download/re-poll)
**One record per:** Unique file URL (SHA-256 deduplication, same as current `DocumentRecord.DocumentId`)

Fields:

| Field | Type | Notes |
|---|---|---|
| `id` | string | = `document_id` |
| `document_id` | string | `doc_{sha256_prefix}` |
| `document_url` | string | Canonical file URL |
| `document_type` | string | Manual, ServiceBulletin, etc. |
| `content_hash` | string | SHA-256 of file bytes |
| `source` | object | `discovery_url`, `discovery_context`, `file_url`, `link_text`, `source_type`, `tab`, `scraped_at` |
| `cross_references` | array | Additional discovery URLs for the same file |
| `classification` | object | `document_type`, `file_format` |
| `file` | object | `local_path`, `filename`, `size_bytes`, `sha256`, `mime_type`, `page_count` |
| `http` | object | `etag`, `last_modified`, `content_type`, `content_length` |
| `timeline` | object | `first_discovered_at`, `last_checked_at`, `last_downloaded_at`, `last_content_changed_at`, `version_count` |
| `link_status` | string | `pending` \| `linked` \| `platform_generic` \| `not_in_catalog` \| `failed` \| `manually_linked` |
| `resolution_strategy` | string? | `xref_slug` \| `filename` \| `page_1` \| `page_2` \| `adi_ocr` \| `manual` \| `override` — set by linker/admin |
| `link_attempted_at` | DateTimeOffset? | Last time the linker attempted this document |
| `link_failure_reason` | string? | Human-readable reason for `failed` / `not_in_catalog` status |
| `linked_by` | string? | AAD object ID of admin who manually linked (manual path only) |
| `linked_at` | DateTimeOffset? | When manual link was applied |
| `override_id` | string? | Points to the `link_overrides` record that drove this decision |

### `scraped_documents` container

**Partition key:** `machine_id`
**Written by:** Linker and admin UI write path only
**One record per:** `(document_id, machine_id)` pair

Fields: extends today's `ScrapedDocumentRecord` with one new field:

| Field | Type | Notes |
|---|---|---|
| `id` | string | `doc_{sha256_prefix}_{machine_id}` — deterministic composite |
| `machine_id` | string | OPDB parent machine ID (partition key) |
| `document_id` | string | SHA-256 of file URL |
| `document_url` | string | Canonical file URL |
| `machine_title` | string | Human-readable machine title |
| `manufacturer` | string | Manufacturer display name |
| `document_type` | string | Manual, ServiceBulletin, etc. |
| `content_hash` | string | SHA-256 of file bytes |
| `last_downloaded_at` | DateTimeOffset? | From Phase 1 scraper timeline |
| `edition` | string? | `"Pro"` \| `"Premium"` \| `"LE"` \| `"CE"` \| `"Vault"` \| null. Null means the document applies to all editions of this machine. Populated by the linker when extractable from link text, filename, or page 1 content; left null otherwise. |

`id` scheme: `doc_{sha256_prefix}_{machine_id}` — deterministic, stable, enables idempotent upserts.

**Edition semantics:** `machine_id` is always the **parent OPDB ID** (e.g. `GRBN-MQR4P` for Godzilla), never an edition alias ID. Edition specificity is expressed via the `edition` tag. The RAG pipeline threads `edition` into chunk metadata so AI Search can filter: a "Godzilla Premium" query returns chunks where `edition = "Premium"` or `edition = null` (all-editions documents), but not `edition = "Pro"`-only chunks. This avoids dependency on complete OPDB alias ID coverage while still giving the retriever edition-aware signal.

The Change Feed on this container drives RAG ingestion exactly as today. No pipeline changes needed.

### `link_overrides` container

**Partition key:** `source_pattern` (= `discovery_url|document_type`, URL-normalized)
**Written by:** Admin UI (on manual link or platform-generic confirmation)
**One record per:** `(source_pattern, document_type)` pair. `id` = `source_pattern` (partition key and id are identical — one record per pattern, upsert semantics).

Fields:

| Field | Type | Notes |
|---|---|---|
| `id` | string | = `source_pattern` |
| `source_pattern` | string | `{discovery_url}\|{document_type}` — partition key |
| `machine_ids` | string[] | OPDB machine IDs. Empty array = confirmed platform-generic. |
| `created_by` | string | AAD object ID |
| `created_at` | DateTimeOffset | |
| `notes` | string? | Optional admin annotation |

---

## Linking Pass

### Trigger

- **ACA Job:** runs on a schedule (e.g. nightly) or triggered after each scraper run completes
- **CLI:** `dotnet run --link-documents` for local dev and manual backfill runs

### Algorithm

For each `scraped_documents_raw` record where `link_status` is `pending`, `failed`, or `not_in_catalog`:

**Tier 0 — Override lookup**

Query `link_overrides` for `source_pattern = discovery_url|document_type`. If found:
- `machine_ids` is non-empty → write linked records, set `link_status: "linked"`, `resolution_strategy: "override"`
- `machine_ids` is empty → set `link_status: "platform_generic"`, `resolution_strategy: "override"`
- No override → continue to tier 1

**Tier 1 — Cross-reference slug** (existing Pass 1 logic from `CatalogBuilder`)

If any cross-reference URL contains `/game/{slug}/` and that slug exists in the `machines` container → definitive match. No PDF reading needed.

**Tier 2 — Filename slug match** (existing Pass 2 logic)

Word-boundary normalized match of known game slugs against the document filename. Longest match wins; ties leave unlinked.

**Tier 3 — Page 1 content extraction**

Extract page 1 text. Scan for known game titles and slugs using word-boundary regex (`\b{token}\b` on normalized text). Handles ~34 of the 91 unlinked docs. Short slugs (≤4 chars: `tron`, `kiss`) require the word-boundary rule — substring matching is insufficient.

**Tier 4 — Page 2 content extraction**

If page 1 text is empty or contains only letterhead/signature content (detected by low token density or known header patterns), extract page 2. Handles ~12 pre-2006 bulletins where the game name appears in the subject line on page 2.

**Tier 5 — ADI OCR**

For documents where text extraction returns garbled or base64-like content (detected by character entropy check), invoke `IDocumentTextExtractor` with OCR mode. Expensive; fires rarely (~2 docs currently).

**Terminal classification**

If no tier resolves:
- Document name/URL matches known platform-generic patterns (EULA, Node Board Update, Guided Setup, Shaker Motors) → `link_status: "platform_generic"`
- Game named in content but not present in `machines` container → `link_status: "not_in_catalog"`, `link_failure_reason` records the game name(s) found
- All else → `link_status: "failed"`, `link_failure_reason` records the last tier attempted and why it stopped

### Edition extraction

At every tier that produces a match, the linker also attempts to extract an edition from the same source that produced the match — link text, filename tail, or page 1/2 content — using the existing `ExtractEditionFromText` / `ExtractEdition` utilities. The extracted edition (or null if none found) is written to the `scraped_documents` record's `edition` field.

For multi-machine documents the edition is typically null (a core node bulletin covers all editions of all named games). For single-machine documents the edition is often determinable from the link text (e.g. "Godzilla Premium Manual" → `"Premium"`).

The admin manual linking UI exposes an optional edition picker alongside the machine picker, so admins can set edition specificity on documents the linker cannot classify automatically.

### Multi-machine fan-out

When a document resolves to N machines, the linker:

1. Writes N records to `scraped_documents` (one per machine, composite `id`), each carrying the resolved `edition` (typically null for multi-machine documents)
2. Sets `scraped_documents_raw.link_status: "linked"`
3. Records `resolution_strategy` and `link_attempted_at`

### Idempotency

- `linked`, `manually_linked`, and `platform_generic` records are skipped — the linker never overwrites a human decision
- `scraped_documents` upserts use the deterministic composite `id` — re-runs do not duplicate
- `link_overrides` lookups are read-only during the run

### Observability

| Metric | Tags |
|---|---|
| `pinwiz.linker.documents_processed_total` | `resolution_strategy`, `link_status` |
| `pinwiz.linker.run_duration_ms` | — |
| `pinwiz.catalog.unlinked_documents` | `link_status` |

---

## Scraper Write Path Changes

### What changes

`ScraperOrchestrator` writes to `scraped_documents_raw` via a new `IRawDocumentRepository` interface instead of building `catalog.json`. The merge/dedup logic from `CatalogBuilder.MergeScrapedItem` moves into `CosmosRawDocumentRepository`:

- Same `document_id` already exists → update `timeline.last_checked_at`, add cross-references if new, update `content_hash` if hash changed
- New `document_id` → insert with `link_status: "pending"`

`games.json` is retired as a file dependency. The linker queries the `machines` Cosmos container directly for slug/title matching (already exists via OPDB sync).

### What is retired

| Artifact | Replacement |
|---|---|
| `catalog.json` | `scraped_documents_raw` container |
| `games.json` | `machines` container (already authoritative) |
| `CatalogBuilder` | `CosmosRawDocumentRepository` (merge/dedup); linker (linking passes) |
| `ScrapedDocumentSeeder` | Not needed — scraper writes directly |
| `--build-catalog` CLI command | Retired |
| `--seed-scraped-documents` CLI command | Retired |

`CatalogBuilder.LinkDocumentsToGames` and `ResolveCoverPageLinksAsync` migrate into the linker's tier 1–3 logic. `NormalizeForMatch`, `ExtractEditionFromText`, `ExtractEdition`, `ExtractGameSlugFromUrl` are shared utilities that move to a `LinkingUtilities` static class in Application.

---

## Admin UI

### Route

Protected admin route, AAD-gated. Accessible only to users with the `PinballWizard.Admin` app role.

### Triage view

Displays all `scraped_documents_raw` records where `link_status` is `failed`, `not_in_catalog`, or `platform_generic`. For each document shows:

- Document type, source URL, link text, file URL
- `link_status` badge and `link_failure_reason`
- `resolution_strategy` attempted
- Extracted page 1 text (and page 2 if attempted) — the exact text the linker saw
- Last attempt timestamp

### Manual linking flow (for `failed` and `not_in_catalog`)

1. Admin selects one or more machines via typeahead search against `machines` container
2. Confirmation dialog summarizes: document → machine(s), with option to add notes
3. On confirm:
   - Writes N records to `scraped_documents` (one per machine)
   - Sets `scraped_documents_raw.link_status: "manually_linked"`, `linked_by`, `linked_at`, `override_id`
   - Writes `link_overrides` record: `{source_pattern, machine_ids[], created_by, created_at, notes}`
4. Change Feed fires immediately — document enters RAG pipeline without waiting for next linker run

### Platform-generic confirmation (for `failed` and review of `platform_generic`)

Admin confirms the document has no machine scope:
- Sets `link_status: "platform_generic"` permanently
- Writes `link_overrides` record with `machine_ids: []`
- Linker never retries; similar future documents resolved via tier 0

### Override management view

Separate admin sub-route listing all `link_overrides` records. Admin can:
- Review existing overrides (source pattern, machines linked, who created it, when)
- Revoke an override (deletes the record; affected `scraped_documents_raw` records revert to `pending` on next linker run)

### Audit trail

Every manual action records `linked_by` (AAD object ID) and `linked_at` on the raw document record. The `override_id` field provides a direct link to the `link_overrides` record that drove the decision.

---

## Backfill

On first deployment, all existing `scraped_documents_raw` records that were previously linked via `catalog.json`/seeder need their `link_status` set to `linked` and the corresponding `scraped_documents` records verified. A one-time backfill migration handles this:

1. Read existing `scraped_documents` records (the linked set today)
2. For each, confirm the corresponding `scraped_documents_raw` record exists (create if not, from catalog.json snapshot)
3. Set `link_status: "linked"` and `resolution_strategy` on the raw record
4. The 91 currently-unlinked documents are inserted into `scraped_documents_raw` with `link_status: "pending"` and the linker runs against them

---

## What Does Not Change

- `ScrapedDocumentChange` DTO — unchanged
- `IRagIngestionPipeline` and `ScrapedDocumentIngestionPipeline` — unchanged
- `RagSourceDocument` change-feed projection — unchanged
- `AiSearchRagIndexer` — unchanged
- `machines` container and OPDB sync — unchanged
- Partition key strategy of `scraped_documents` — unchanged (`machine_id`)

---

## Open Items

- `24-2` slug — confirm this corresponds to a real machine before the linker trusts matches to it
- `avengers` vs `avengers-infinity-quest` — verify whether plain `avengers` slug exists in the machines catalog or is a data artifact
- Games not in catalog (T3, Simpsons Pinball Party, Pirates of the Caribbean, SAM system games, Primus) — separate decision whether to add them; the linker correctly classifies these as `not_in_catalog` and they can be linked once catalog entries exist
- Page-count field on `DownloadedFileInfo` — currently nullable; the linker needs to know whether a document has ≥2 pages before attempting tier 4. Populate this during download.
