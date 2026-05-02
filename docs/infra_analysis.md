# Pinball Wizard — Phase 2 Infrastructure Plan

This document describes the Azure infrastructure that will host Phase 2 (the
RAG indexing pipeline and query API) of Pinball Wizard. Phase 1's scraper
output (`catalog.json`, `games.json`, downloaded files) is the input contract.

> **Decisions locked 2026-05-02.** This doc reflects the locked architecture:
> Azure Container Apps + AI Search Basic, East US 2, $200/mo cost cap. See
> the project memory `project_phase2_architecture_decisions.md` for the full
> rationale and the levers explicitly chosen *not* to pursue.

---

## 1. Topology

Single ACA environment hosting the API/UI, the scraper, and the indexer —
one platform for everything. Deployed via Azure Deployment Stacks.

### Shared resources (`main-shared.bicep` → `rg-pinball-wizard-shared`)

| Resource | Service | Notes |
| --- | --- | --- |
| AI Models | Azure OpenAI | `gpt-4o-mini` default, `gpt-4.1` escalation for hard queries (router) |
| Embeddings | Azure OpenAI | `text-embedding-3-large` (3072-dim) |
| Search | Azure AI Search **Basic** ($74/mo) | Hybrid search + semantic ranker (included on Basic). The chosen backend — not optional. |
| Container Registry | ACR Basic | Images for ACA App + ACA Jobs |
| Secrets | Key Vault | Standard, Entra ID auth only |
| Monitoring | Log Analytics + App Insights | 1GB/mo capped; diagnostic settings route here |

### Per-environment (`main-env.bicep` → `rg-pinball-wizard-{prod|dev}`)

| Resource | Service | Config |
| --- | --- | --- |
| ACA Environment | Container Apps Env | Consumption profile, single environment |
| API + UI | ACA App `pinball-api` | ASP.NET Core (API) + Razor (UI), HTTPS ingress, **min=1 live / min=0 during build** (Bicep parameter) |
| Scraper | ACA Job `pinball-scraper` | Schedule trigger, daily 03:00. Replaces the cron-on-VM Phase 1 deployment shape. |
| Indexer | ACA Job `pinball-indexer` | Schedule trigger, daily 03:30 (after scraper) |
| Storage | Blob Storage | Containers: `pinball-raw` (catalog.json + downloads), `pinball-processed` (chunk metadata), `pinball-static` (UI assets if needed) |

### Deployment posture: minimal/public

- Region: **East US 2**
- `enablePrivateNetworking = false` — public endpoints
- `searchBackend = 'azure-ai-search-basic'` — only supported backend; pgvector path was dropped
- `apiMinReplicas` — Bicep parameter, `0` during active build, `1` in live
- `deployAcr = true` — ACR hosts ACA App + Job images
- Custom domain: bound to ACA App with managed cert

---

## 2. Security model

**Zero-secret architecture.** Every service-to-service connection uses Managed Identity + RBAC:

| Identity | Key Vault | Azure OpenAI | Storage | AI Search | ACR |
| --- | --- | --- | --- | --- | --- |
| `pinball-api` MI | Secrets User | OpenAI User | Blob Data Contributor | Index Data Reader (query) | AcrPull |
| `pinball-indexer` MI | Secrets User | OpenAI User | Blob Data Reader | Index Data Contributor | AcrPull |
| `pinball-scraper` MI | (none) | (none) | Blob Data Contributor | (none) | AcrPull |

- `disableLocalAuth = true` on Azure OpenAI — no API keys
- `allowSharedKeyAccess = false` on Storage — Entra ID only
- `disableLocalAuth = true` on AI Search — Entra ID only
- ACA Apps and Jobs all run with system-assigned managed identities; no shared secrets in container env vars

---

## 3. Data flow (RAG pipeline)

```text
pinball-scraper (ACA Job, daily 03:00)
    ↓ writes
pinball-raw/        ← Scraper output (PDFs, ZIPs, SPKs) + catalog.json + games.json
    ↓ trigger
pinball-indexer (ACA Job, daily 03:30 — SHA-driven idempotent)
    ↓ PdfPig text extraction → page-aware chunking (2000 chars / 400 overlap)
    ↓ + metadata-card chunk per game (synthesized prose)
[Azure OpenAI: text-embedding-3-large]
    ↓
[Azure AI Search Basic — index: pinball_chunks]
    ↑ hybrid search + semantic ranker
pinball-api (ACA App)
    ↓ POST /query
[Azure OpenAI: gpt-4o-mini default → gpt-4.1 escalation]
    ↓
Razor UI ← attributed response w/ clickable sternpinball.com citations
```

API and indexer share the same AI Search index. There is no alternative backend.

---

## 4. Phase 2 Architecture

### 4.1 Ingestion pipeline

```text
Scraper Output (Phase 1, ACA Job)   Indexer (Phase 2, ACA Job)
─────────────────────────────       ──────────────────────────
catalog.json ─────────────────────► CatalogParser (reads catalog.json from blob)
games.json   ─────────────────────► PdfChunker (PdfPig → page-aware chunks,
downloads/   ─────────────────────►              2000 chars / 400 overlap,
                                                 metadata-card chunks per game)
                                    EmbeddingService (text-embedding-3-large)
                                    ProvenanceMapper (chunk → document_id → catalog)
                                    AiSearchIndexer (upsert into pinball_chunks)
                                    SHA-driven idempotency: skip unchanged docs
```

### 4.2 Query pipeline

```text
User: "What's the wiring for Stranger Things trough opto?"

  → POST /query (pinball-api ACA App)
      ├── EmbeddingService → embed query (text-embedding-3-large)
      ├── SearchService → AI Search hybrid search + semantic ranker
      │     Returns: [chunk_id, document_id, page_range, score, semantic_score]
      ├── ThresholdGate → if top semantic_score < 0.6, return "no answer found"
      │                   with sternpinball.com search link (no hallucination)
      ├── ProvenanceMapper → resolve document_id → full attribution chain
      │     Returns: file_url, discovery_url, game, edition, doc_type
      └── CompletionService → router (gpt-4o-mini default,
                                       gpt-4.1 escalation on hard queries)
            Returns: answer + citations with clickable sternpinball.com links
```

### 4.3 Search index schema (Azure AI Search)

One index, `pinball_chunks`. All metadata needed for filtering and citation
is denormalized onto each chunk so query-time joins are not required.

```jsonc
{
  "name": "pinball_chunks",
  "fields": [
    { "name": "chunk_id",          "type": "Edm.String",  "key": true },
    { "name": "document_id",       "type": "Edm.String",  "filterable": true },
    { "name": "content",           "type": "Edm.String",  "searchable": true, "analyzer": "en.microsoft" },
    { "name": "section_title",     "type": "Edm.String",  "searchable": true },
    { "name": "page_start",        "type": "Edm.Int32",   "filterable": true },
    { "name": "page_end",          "type": "Edm.Int32",   "filterable": true },
    { "name": "content_category",  "type": "Edm.String",  "filterable": true, "facetable": true },

    // Denormalized provenance (avoid query-time lookup against catalog.json)
    { "name": "file_url",          "type": "Edm.String",  "retrievable": true },
    { "name": "discovery_url",     "type": "Edm.String",  "retrievable": true },
    { "name": "document_type",     "type": "Edm.String",  "filterable": true, "facetable": true },
    { "name": "file_format",       "type": "Edm.String",  "filterable": true, "facetable": true },
    { "name": "tab",               "type": "Edm.String",  "filterable": true, "facetable": true },

    // Denormalized game metadata (faceted browse, pre-filter)
    { "name": "game_slug",         "type": "Edm.String",  "filterable": true, "facetable": true },
    { "name": "game_title",        "type": "Edm.String",  "searchable": true, "retrievable": true },
    { "name": "edition",           "type": "Edm.String",  "filterable": true, "facetable": true },

    // Vector field — text-embedding-3-large dimensionality
    { "name": "content_vector",    "type": "Collection(Edm.Single)", "searchable": true,
      "vectorSearchDimensions": 3072, "vectorSearchProfile": "default-vector-profile" }
  ],
  "semantic": { "configurations": [ { "name": "default", "prioritizedFields": {
      "titleField":          { "fieldName": "section_title" },
      "prioritizedContentFields":   [ { "fieldName": "content" } ],
      "prioritizedKeywordsFields":  [ { "fieldName": "game_title" }, { "fieldName": "document_type" } ]
  }}]}
}
```

**Why denormalize provenance onto chunks:** AI Search has no joins. Storing
`file_url`, `game_slug`, `edition`, etc. on each chunk lets the API return
a full citation in one round trip. The canonical source remains
`catalog.json` (and the `pinball-raw` blob); the chunk row is a derived
view that gets rebuilt from scratch if anything goes wrong.

### 4.4 RAG attribution chain (end-to-end)

```text
User question
  → embed (text-embedding-3-large)
  → AI Search hybrid + semantic ranker → top-K chunks
  → each chunk carries denormalized provenance (no join needed):
      file_url, discovery_url, game_slug, game_title, edition, document_type, page_start/end

Response format:
  "The trough opto board uses a 12V supply with..."

  Source: Stranger Things Pro Manual, pp. 23-24
     Direct link: https://sternpinball.com/wp-content/.../StrangerThings_Pro_web.pdf
     Found at: https://sternpinball.com/game/stranger-things/ → Specs & Manual tab
```

---

## 5. Deployment plan

### Step 1: Phase 1 scraper as ACA Job

The Phase 1 scraper container runs as `pinball-scraper` (ACA Job, schedule trigger). On each run it writes `catalog.json`, `games.json`, and downloaded files into the `pinball-raw` blob container. This replaces the cron-on-VM deployment shape originally planned for Phase 1.

### Step 2: Provision Phase 2 infrastructure

Deploy `main-shared.bicep` + `main-env.bicep` via Azure Deployment Stack to create the shared resource group (Search, OpenAI, ACR, Storage, Key Vault, monitoring) and the per-env resource group (ACA environment + apps + jobs).

### Step 3: Upload scraper output to Blob Storage

```bash
az storage blob upload-batch \
  --account-name <storage-account> \
  --destination pinball-raw \
  --source ./data/downloads
```

And separately upload `catalog.json` + `games.json` to the same container.

### Step 4: Ingestion pipeline

- Push the AI Search index schema (`pinball_chunks`) — see §4.3
- ACA Job `pinball-indexer` reads `catalog.json` from blob → for each doc where SHA changed since last index: extract PDF text (PdfPig) → chunk (page-aware, 2000 chars / 400 overlap) → embed (`text-embedding-3-large`) → upsert into AI Search
- Generate one synthesized "metadata-card" chunk per game (combining title, editions, designer, theme) so semantic queries can reach pure-metadata facts
- Idempotent on `document_id` so a re-run is safe; SHA-driven so unchanged content does not re-embed

### Step 5: Query endpoint

- Hybrid retrieval: vector similarity + BM25 + AI Search semantic ranker (Basic tier includes it)
- Threshold gate: top semantic_score < 0.6 → return "no answer found" with sternpinball.com search link (no hallucination)
- RAG prompt with denormalized provenance from the chunk row → completion router (`gpt-4o-mini` default, `gpt-4.1` for hard queries)

### Step 6: UI

- Razor Pages UI inside the same `pinball-api` ACA App (no separate frontend container)
- Chat interface with attributed answers, citation chips clickable to sternpinball.com
- Faceted sidebar: filter by game, edition, document type, tab, content category
- Custom domain bound via ACA managed certificate

---

## 6. Cost estimate

**Hard cap: $200/mo. Anomaly alert at $150/mo.**

| Component | SKU | Monthly |
| --- | --- | --- |
| Azure AI Search | Basic | $74 |
| ACA App `pinball-api` (0.5 vCPU + 1GB) | Consumption, **min=1 live / min=0 build** | ~$35 live / ~$3-10 build |
| ACA Jobs `pinball-scraper` + `pinball-indexer` | Consumption, schedule-triggered | <$1 combined |
| Container Registry | Basic | $5 |
| Storage Account | Standard LRS | $2-5 |
| Azure OpenAI: embeddings | `text-embedding-3-large`, SHA-gated re-embed | ~$5 one-time + ~$0.50/mo incremental |
| Azure OpenAI: completions | `gpt-4o-mini` default + `gpt-4.1` router (~15-20% of queries) | $10-40 variable |
| Application Insights | 1GB/mo cap | $2-5 |
| Log Analytics | 1GB/mo cap | $2-3 |
| Key Vault | Standard | <$1 |
| **Steady-state (live)** | | **~$130-170/mo** |
| **Steady-state (active build, min=0)** | | **~$95-130/mo** |

**Decisions explicitly NOT taken** (and the cost they would have added):

- AI Search Standard — would add ~$170/mo for marginal quality gain at this corpus size
- Multi-region failover — would ~2x infra cost; single region accepted
- Separate dev environment with its own AI Search — would add another $74/mo; iterate on prod from a feature branch with min=0 instead
- Custom embedding fine-tuning — would be entire engineering investment for ~2% recall
- OCR pipeline for scanned manuals — deferred; documented as known gap
- Authenticated user accounts — anonymous v1; add Cloudflare Turnstile only if abuse appears

This trade space is the cost/value showcase — picking the managed service (AI Search Basic) that ships the semantic ranker rather than building it on pgvector, and refusing to gold-plate features the corpus size doesn't warrant.

---

## 7. Engineering principles

These are the patterns Phase 2 will follow — standard Azure best practices, not project-specific:

1. **Managed Identity everywhere** — no API keys, no connection strings with passwords
2. **Entra ID auth** at every authentication boundary
3. **Feature flags in Bicep** — toggleable components via parameter files
4. **Deployment Stacks** — lifecycle-managed, drift-protected
5. **AVM modules** — Azure Verified Modules for all resource types where available
6. **Subscription guard** — `assert` to prevent wrong-subscription deployment
7. **Pre-flight checks** — auto-purge soft-deleted resources before deploy
8. **Retry with backoff** — transient Azure errors handled automatically
9. **Shared / per-environment split** — cross-cutting resources vs. isolated ones
10. **Diagnostic settings on everything** — all logs route to Log Analytics
