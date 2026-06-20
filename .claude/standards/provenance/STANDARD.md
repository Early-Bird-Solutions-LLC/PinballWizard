---
name: provenance
id-prefix: PROV
status: active
applies-to:
  - "src/PinballWizard.Core/**"
  - "src/PinballWizard.Infrastructure/Scraping/**"
  - "src/PinballWizard.Infrastructure/Persistence/**"
  - "src/PinballWizard.Infrastructure/Rag/**"
---

# Provenance Standard

Every captured item must trace back to its source URL. The provenance chain
is the foundation of Phase 2 RAG citations.

**RULE PROV-01** (source-url-traceable)
WHEN:   a data path constructs, maps, or persists a ScrapedItem / catalog entry / RAG chunk
THEN:   Source, DiscoveryUrl, DiscoveryContext, GameSlug travel with the record end-to-end
NEVER:  drop or null a provenance field in a DTO projection or mapping
CHECK:  (qualitative — /local-review) — inspect new mappers/DTOs for dropped Source/DiscoveryUrl/DiscoveryContext/GameSlug
SEV:    🔴
REF:    INVARIANTS#1 · ADR-0002 · ADR-0004

**RULE PROV-02** (deterministic-id)
WHEN:   a new captured item type is introduced
THEN:   its ID is SHA-256(canonical_url.ToLower())[0:16] with the doc_/mch_ prefix
NEVER:  use a random GUID or a non-URL-derived ID for a captured item
CHECK:  rg -n "Guid.NewGuid|Random" src/PinballWizard.Infrastructure/Scraping/ src/PinballWizard.Infrastructure/Persistence/
SEV:    🔴
REF:    INVARIANTS#1 · ADR-0002

**RULE PROV-03** (catalog-contract-boundary)
WHEN:   code reads or writes the Phase1↔Phase2 boundary (catalog.json, machines / ingestion_sources containers)
THEN:   treat it as the locked API contract — additive fields only, provenance preserved
NEVER:  reshape or strip the catalog contract to suit a consumer
CHECK:  (qualitative — /local-review) — verify catalog/machines/ingestion_sources schema changes are additive
SEV:    ⚠️
REF:    INVARIANTS#8

## Definition of Done

- PROV-01: new/changed mappers carry all four provenance fields end-to-end.
- PROV-02: no `Guid.NewGuid`/`Random` ID generation for captured items.
- PROV-03: catalog-boundary changes are additive and provenance-preserving.
