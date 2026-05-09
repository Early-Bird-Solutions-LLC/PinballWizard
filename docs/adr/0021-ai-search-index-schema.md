# 0021 — AI Search index schema for Phase 4 RAG

**Status:** Accepted
**Date:** 2026-05-07

## Context

Phase 4 indexes chunks (and metadata cards) into AI Search Basic.
The schema needs to support:

- **Vector retrieval** at 3072d per [ADR-0020](0020-embedding-model.md)
- **Semantic ranker** for hybrid retrieval quality (Phase 4 default
  per design conversation)
- **Faceted filtering** by manufacturer / machine / document type
  / page so sub-agent-aware retrieval can constrain the search
  space (e.g., Repair queries filter to `document_type=manual`
  AND `machine_title="Godzilla (Premium)"`)
- **Citation-anchor display** — chunks must carry the metadata
  needed to render `<doc>.pdf p.42–43, § Foo Mode rules` per
  [ADR-0019](0019-hybrid-chunking.md) and
  [ADR-0022](0022-citation-extraction.md)
- **Versionable schema.** Schema-breaking changes mid-corpus on a
  multi-GB index are expensive — the strategy must prevent
  in-place migrations.

## Decision

Index name **`pinwiz-rag-v1`**.

### Schema

| Field | Type | Searchable | Filterable | Facetable | Sortable | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| `chunk_id` | `Edm.String` (key) | no | yes | no | no | SHA-256(machine_id ‖ document_id ‖ page_range ‖ chunk_index) |
| `machine_id` | `Edm.String` | no | yes | yes | no | OPDB ID of the machine |
| `machine_title` | `Edm.String` | yes | yes | yes | yes | "Godzilla (Premium)" |
| `manufacturer` | `Edm.String` | no | yes | yes | yes | "Stern", "JJP", etc. |
| `document_id` | `Edm.String` | no | yes | no | no | `scraped_documents` ID for PDFs; `metadata_card_<machine_id>` for synthesized cards |
| `document_url` | `Edm.String` | no | no | no | no | Original source URL — Wizard renders this in the citation |
| `document_type` | `Edm.String` | no | yes | yes | no | `manual` / `service_bulletin` / `metadata_card`; Phase 4 populates all three for Stern machines in the curated subset and `manual` + `metadata_card` for non-Stern; Phase 4.5 extends bulletin coverage to other manufacturers |
| `page_start` | `Edm.Int32` | no | yes | yes | yes | First page (inclusive) of chunk |
| `page_end` | `Edm.Int32` | no | yes | yes | yes | Last page (inclusive) of chunk |
| `section_heading` | `Edm.String` | yes | yes | yes | no | "Coil Replacement", "Wizard Mode Rules", etc. (empty for no-outline fallback) |
| `content` | `Edm.String` | yes | no | no | no | The chunk text |
| `content_embedding` | `Collection(Edm.Single)` | n/a | n/a | n/a | n/a | 3072d, HNSW algorithm, cosine similarity |
| `last_scraped_utc` | `Edm.DateTimeOffset` | no | yes | no | yes | Source document's `Timeline.LastDownloadedAt` from the Phase 1 scraper provenance record. "Scraped" = last byte-level fetch; `LastDownloadedAt` is the correct semantic because `LastContentChangedAt` is null when content has never changed (a new document with only one fetch would show null freshness), whereas `LastDownloadedAt` is populated on every successful fetch. Filterable + sortable for freshness-sort queries; NOT searchable (timestamps are opaque to text search); NOT facetable (continuous timestamp). Null for chunks indexed before Wave 2 PR-C3; populated going forward per ADR-0025 § 6 zero-migration-cost schema add. |

### Vector configuration

- Algorithm: **HNSW** (the default; balanced recall vs. speed for
  Basic SKU)
- Metric: **cosine similarity** (matches OpenAI embedding model
  norm-invariance behavior)
- Profile: `pinwiz-rag-vector-profile-v1`

### Semantic ranker configuration

- Enabled (Phase 4 default per design conversation)
- Configuration name: `pinwiz-rag-semantic-v1`
- `prioritized_content_fields = [content]`
- `prioritized_keyword_fields = [machine_title, section_heading]`
- `title_field = section_heading`

### Search defaults

- Hybrid retrieval: vector (`content_embedding`) + keyword
  (`content`, `machine_title`, `section_heading`) + semantic
  ranking. Per [ADR-0022](0022-citation-extraction.md), the
  retrieval-set bookkeeping for citations consumes the resulting
  `RetrievedChunk[]`.
- Highlighting on `content` so the Wizard can surface the matched
  snippet in the citation card (Phase 5 UI).

### Versioning strategy

Schema-breaking changes spin up `pinwiz-rag-v2`:

1. Create the new index.
2. Re-ingest the corpus into v2 (via the Cosmos Change Feed
   Function). Idempotent SHA-driven upserts make this safe.
3. Switch `IRagRetriever` reads from `v1` to `v2`. (Brief dual-read
   period optional; cutover can be hard if eval is green.)
4. Delete `v1` after cutover stable for one operational cadence.

This avoids in-place ALTER-style migrations — AI Search doesn't
support them anyway, and atomically swapping index names is
operationally simple.

## Consequences

**Positive:**

- Hybrid retrieval (vector + semantic + keyword) is supported by
  the schema with no further configuration.
- Faceted filtering enables sub-agent-aware retrieval. A Repair
  query for "Godzilla coil replacement" filters to `manufacturer
  = Stern` AND `machine_title = "Godzilla (Premium)"` AND
  `document_type = manual`, dramatically narrowing the vector
  search.
- Page-anchor citations surface natively via `page_start` /
  `page_end` / `section_heading`.
- Versioned index name means we never have to attempt live
  in-place schema migration. The cost of a v2 cutover is a single
  re-ingestion; cheap given idempotency.
- The schema's `document_type` enum supports `manual` /
  `service_bulletin` / `metadata_card` from v1. Phase 4 populates
  all three for the curated subset's Stern machines (Stern's
  `ServiceBulletinScraper` already produced bulletin records in
  Phase 1); non-Stern manufacturers populate `manual` +
  `metadata_card` in Phase 4 and gain bulletin coverage in
  Phase 4.5.

**Negative:**

- **Schema-breaking changes incur full re-ingestion cost.** For
  Phase 4 (curated subset, ~$0.07/run) this is trivial; for Phase
  4.5 full corpus the re-ingestion cost is $5–$10 per run. Worth
  documenting as a Phase 4.5 budgeting item.
- The schema doesn't include some plausibly-useful fields
  (`prev_section_heading` for context-aware ranking, `theme` for
  thematic faceting, `year_released` for era filtering). Adding
  any of these requires a v2 cutover. The trade-off vs. a
  larger-but-future-proof v1 is intentional: ship lean, expand on
  evidence.
- Dual-read transition cost during a v1→v2 cutover (if a soft
  cutover is chosen) requires the retriever to query both indexes
  briefly. Hard cutover avoids this; default hard.
- AI Search Basic index limit (2 GB total) constrains how much v1
  can grow before Phase 4.5 forces a Standard upgrade or
  multi-index sharding — see [ADR-0020](0020-embedding-model.md)
  for the trip-wire analysis.

## Alternatives considered

- **AI Search Standard SKU** (more capacity, more features such
  as built-in vectorizer integration). Rejected per Phase 2
  architecture lock (deferred features index): ~3× cost; Basic
  fits Phase 4 corpus comfortably; revisit when corpus exceeds
  Basic's 2 GB / 15-index / 500 MB-per-index limits.
- **Elasticsearch / Weaviate / Pinecone.** Rejected per Phase 2
  architecture lock — AI Search includes semantic ranker
  out-of-box, integrates natively with Foundry, and the cost
  envelope already fits within the $300–$400/mo cap.
- **Single composite `content` field** (no chunk metadata).
  Rejected — loses citation-anchor info; can't render
  page-specific citations; can't facet retrieval.
- **Vectors-only schema** (no faceted fields). Rejected —
  sub-agent-aware retrieval needs filters; without facets the
  vector search has to do all the work and accuracy degrades on
  ambiguous queries.
- **Vectorize manufacturer / theme / year** (additional
  vector fields per facet). Rejected for v1 — those are filter +
  facet fields; vectorizing them adds cost and storage without
  clear retrieval-quality benefit. Phase 4.5 revisits if
  user-traffic queries demonstrate need.
- **In-place schema mutation** (ALTER-style). Rejected — AI
  Search doesn't support it; even if it did, multi-GB index
  mutations are operationally risky.
- **Skip semantic ranker** (vector-only retrieval). Rejected —
  hybrid (vector + semantic + keyword) is the showcase posture
  per the design conversation; the small additional latency is
  acceptable. P4-R7 risk tracks an A/B re-evaluation at H2.

## References

- [ADR-0014](0014-microsoft-foundry-orchestration.md) — Foundry
  orchestrator that produces the queries
- [ADR-0019](0019-hybrid-chunking.md) — chunking; the metadata
  fields originate in chunks
- [ADR-0020](0020-embedding-model.md) — vector field dimension
  rationale
- [ADR-0022](0022-citation-extraction.md) — `chunk_id` →
  citation surface
- [ADR-0023](0023-citation-required-guardrail.md) — empty
  retrieval set ⇒ refusal
- [build-spec.md § Phase 4](../build-spec.md) — scope items 3
  (this ADR), 16 (embedding pipeline + index population), 18
  (Change Feed Function ingests into this schema), 20 (query
  client consumes this schema)
- [build-spec.md § Phase 4.5](../build-spec.md) — schema's
  forward-compatibility design intent
