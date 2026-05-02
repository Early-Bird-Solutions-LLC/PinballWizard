# 0002 — Deterministic document IDs derived from canonical file URL

**Status:** Accepted
**Date:** 2026-05-02 (codifies a decision implemented earlier in the project)

## Context

PinballWizard discovers documents (PDFs, ZIPs, SPKs, README files) from
multiple source pages on `sternpinball.com`. The same physical document
is often linked from more than one place — for example, a Stranger
Things Pro manual appears on `/manuals/` AND on the
`/game/stranger-things/` Specs & Manual tab.

We need a stable identifier for each document so that:

1. Cross-page discovery of the same document collapses to one record,
   not duplicates.
2. Re-runs of the scraper produce the same identifier for the same
   document — the catalog is a function of source-site state, not
   scrape-run state.
3. The Phase 2 RAG pipeline can join chunks back to documents reliably
   across re-indexes.
4. The identifier is generatable by anyone who has the file URL,
   without requiring access to the catalog.

## Decision

A document's identifier is derived deterministically from its **canonical
file URL**, with the following formula:

```
document_id = "doc_" + SHA-256(canonical_file_url.ToLower())[0..16]
```

Where:

- The "canonical file URL" is the absolute, scheme-included URL of the
  file on the source site (e.g.,
  `https://sternpinball.com/wp-content/uploads/2023/05/StrangerThings_Pro_web.pdf`).
- `.ToLower()` is invariant-culture lowercase. Many web servers serve
  the same file under URL paths that differ only in case; we treat
  those as the same document.
- The hash is SHA-256, taken of the UTF-8 bytes of the lowercased URL.
- We use the first 16 hex characters (64 bits) for the suffix. With
  fewer than 100K expected documents in v1, collision probability is
  negligible (<10⁻⁹).
- The `doc_` prefix is constant; it makes IDs visually distinguishable
  from raw hashes in logs and JSON dumps.

When the same document URL is found on more than one page, the
`DocumentRecord` for that ID is updated with a new entry in
`cross_references[]` rather than producing a second record.

## Consequences

**Positive:**
- Idempotent across re-runs. The catalog at run N+1 has the same IDs
  as run N for any document whose URL hasn't changed.
- Cross-source dedup is automatic — no fuzzy matching needed.
- A future system (Phase 2 RAG, an external integration) can compute
  an ID from a URL alone, without needing to query our catalog.
- Logs and stack traces include human-tractable IDs.

**Negative:**
- A document whose URL changes (e.g., the source site reorganizes
  paths) gets a new ID. The provenance chain for the old URL is
  preserved in catalog history but the link is lost. This is acceptable
  — the URL change is itself the meaningful event, and the timeline /
  cross_references mechanism captures the renaming.
- A document served at multiple genuinely-different URLs (e.g., a
  mirror) is treated as multiple documents. This has not been observed
  on `sternpinball.com` in practice and would be addressed by a
  canonicalization pass if it became a problem.
- 64-bit truncated SHA is not cryptographically collision-resistant —
  but document IDs are not a security boundary. They are stable
  identifiers, not commitments.

## Alternatives considered

- **UUIDs minted at first discovery.** Rejected — would require the
  catalog to be the source of truth for ID assignment, and re-runs on
  empty state would produce different IDs each time.
- **Content-hash-based IDs (SHA of the file bytes).** Rejected — the
  file bytes change when the source site updates a document, which is
  exactly the case where we want ID stability so we can show "version
  count" and "last content changed" in the timeline.
- **Source-site primary keys (e.g., WordPress post IDs).** Rejected —
  not consistently available, requires DOM inspection on every page,
  brittle against site changes.
