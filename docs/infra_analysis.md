# Pinball Wizard — Phase 2 Infrastructure Plan

This document describes the Azure infrastructure that will host Phase 2 (the RAG indexing pipeline and query API) of Pinball Wizard. The scraper (Phase 1) runs as a Docker container with cron and does not need any of this infrastructure — it produces files and `catalog.json` on a mounted volume that Phase 2 will ingest.

---

## 1. Topology

A two-tier deployment using Azure Deployment Stacks:

### Shared resources (`main-shared.bicep` → `rg-pinball-wizard-shared`)
| Resource | Service | Notes |
|----------|---------|-------|
| AI Models | Azure AI Services (Foundry) | gpt-4.1 / gpt-4.1-mini for chat completion |
| Embeddings | Azure AI Services | text-embedding-3-large (3072-dim) |
| Search | Azure AI Search (Basic) | Semantic ranking enabled (optional — pgvector is the default backend) |
| Secrets | Key Vault | Standard, Entra ID auth only |
| Monitoring | Log Analytics + App Insights | All diagnostic settings route here |

### Per-environment (`main-env.bicep` → `rg-pinball-wizard-{prod|dev}`)
| Resource | Service | Config |
|----------|---------|--------|
| Compute | App Service Plan | B1 Basic (1 vCPU, 1.75 GB) |
| Web App | App Service (.NET 10) | Blazor frontend, Always On |
| API App | App Service (.NET 10) | ASP.NET Core backend, SignalR for streaming responses |
| Database | PostgreSQL Flexible Server | Burstable B1ms, with `pgvector` + `uuid-ossp` + `pg_trgm` extensions |
| Storage | Blob Storage | Containers: `pinball-raw`, `pinball-processed`, `pinball-indexes` |

### Deployment posture: minimal/public
- `enablePrivateNetworking = false` — public endpoints
- `databaseType = 'postgresql'` — pgvector for vector search (default)
- `deployVNet = false` — no VNet, NSGs, or private DNS
- `deployApplicationGateway = false` — no WAF
- `deployAiSearch = true` — AI Search deployed as an alternative to pgvector
- `deployAcr = false` — no container registry needed (the scraper ships its own image)

---

## 2. Security model

**Zero-secret architecture.** Every service-to-service connection uses Managed Identity + RBAC:

| Identity | Key Vault | AI Services | Storage | AI Search |
|----------|-----------|-------------|---------|-----------|
| Web App MI | Secrets User | OpenAI User + AI Services User | Blob Data Contributor | Index Data Contributor |
| API App MI | Secrets User | OpenAI User + AI Services User | Blob Data Contributor | Index Data Contributor + Service Contributor |

- `disableLocalAuth = true` on AI Services — no API keys
- `allowSharedKeyAccess = false` on Storage — Entra ID only
- `disableLocalAuth = true` on AI Search — Entra ID only
- PostgreSQL: Entra ID auth enabled, password auth retained only for initial provisioning

---

## 3. Data flow (RAG pipeline)

```
pinball-raw/        ← Scraper output (PDFs, ZIPs, SPKs) + catalog.json + games.json
    ↓
[API App]           ← PDF parsing, chunking, embedding
    ↓
pinball-processed/  ← Parsed/chunked content (JSON)
    ↓
[Embedding Model]   ← text-embedding-3-large
    ↓
pinball-indexes/    ← Vector index artifacts (when using AI Search backend)
    ↓
[pgvector OR AI Search] ← Vector similarity search
    ↓
[GPT model]         ← RAG-augmented response generation with attribution
```

The API App is configured for either backend:
- `SEARCH_BACKEND = 'pgvector' | 'azure-ai-search'`
- `AZURE_AI_SEARCH_ENDPOINT` (when using AI Search)
- PostgreSQL connection string (when using pgvector)

---

## 4. Phase 2 Architecture

### 4.1 Ingestion pipeline

```
Scraper Output (Phase 1)          API App
─────────────────────────          ─────────────────────
catalog.json ──────────────────►  IngestController.cs
games.json   ──────────────────►    ├── CatalogParser.cs (reads catalog.json)
downloads/   ──────────────────►    ├── PdfChunker.cs (PdfPig → page-aware chunks)
                                    ├── EmbeddingService.cs (text-embedding-3-large)
                                    ├── ProvenanceService.cs (writes DocumentRecord rows)
                                    └── IndexService.cs (writes to pgvector or AI Search)
```

### 4.2 Query pipeline

```
User: "What's the wiring for Stranger Things trough opto?"

  → QueryController.cs
      ├── EmbeddingService.cs → embed query
      ├── SearchService.cs → vector + BM25 hybrid search
      │     Returns: [chunk_id, document_id, page_range, score]
      ├── ProvenanceService.cs → resolve document_id → full attribution chain
      │     Returns: file_url, discovery_url, game, edition, doc_type
      └── CompletionService.cs → RAG prompt → gpt-4.1
            Returns: answer + citations with clickable sternpinball.com links
```

### 4.3 Database schema (PostgreSQL + pgvector)

```sql
-- Provenance tables
CREATE TABLE pinball_documents (
    document_id TEXT PRIMARY KEY,      -- deterministic hash of file_url
    source JSONB NOT NULL,             -- discovery_url, file_url, context, etc.
    classification JSONB NOT NULL,     -- doc_type, content_categories
    game JSONB,                        -- title, slug, edition
    file JSONB NOT NULL,               -- local_path, filename, size, sha256
    http JSONB,                        -- last_modified, etag
    timeline JSONB NOT NULL,           -- first/last discovered/downloaded/changed
    cross_references JSONB DEFAULT '[]',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE pinball_games (
    slug TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    game_page_url TEXT,
    editions JSONB DEFAULT '[]',
    features JSONB DEFAULT '[]',
    images JSONB DEFAULT '[]',
    scraped_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Chunks table with vectors
CREATE TABLE pinball_chunks (
    chunk_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id TEXT REFERENCES pinball_documents(document_id),
    content TEXT NOT NULL,
    page_range INT4RANGE,              -- [start, end) page range
    section_title TEXT,
    content_category TEXT,             -- 'rules', 'schematics', 'parts', 'wiring'
    embedding VECTOR(3072),            -- text-embedding-3-large dimension
    metadata JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Vector similarity index
CREATE INDEX idx_pinball_chunks_embedding
    ON pinball_chunks USING ivfflat (embedding vector_cosine_ops)
    WITH (lists = 100);

-- Full-text search for hybrid retrieval
CREATE INDEX idx_pinball_chunks_content_fts
    ON pinball_chunks USING gin (to_tsvector('english', content));
```

### 4.4 RAG attribution chain (end-to-end)

```
User question
  → embed → vector search → top-K chunks
  → each chunk carries document_id
  → JOIN pinball_documents ON document_id
  → extract: source.file_url, source.discovery_url, game.title, game.edition

Response format:
  "The trough opto board uses a 12V supply with..."

  Source: Stranger Things Pro Manual, pp. 23-24
     Direct link: https://sternpinball.com/wp-content/.../StrangerThings_Pro_web.pdf
     Found at: https://sternpinball.com/game/stranger-things/ → Specs & Manual tab
```

---

## 5. Deployment plan

### Step 1: Scraper (Phase 1) — no Azure
Docker container with cron on any host (dev machine, cheap VM, ACI). Outputs files + `catalog.json` + `games.json` to a mounted volume.

### Step 2: Provision Phase 2 infrastructure
Deploy `main-shared.bicep` + `main-env.bicep` to create the resource groups, Postgres, Storage, App Services, AI Services, and Key Vault.

### Step 3: Upload scraper output to Blob Storage
```
az storage blob upload-batch \
  --account-name <storage-account> \
  --destination pinball-raw \
  --source ./data/downloads
```
And separately upload `catalog.json` + `games.json` to the same container.

### Step 4: Ingestion pipeline
- Add EF Core migrations for the three tables above
- API App reads `catalog.json` from blob → parses each PDF → chunks → embeds → indexes
- Idempotent on `document_id` so a re-run is safe

### Step 5: Query endpoint
- Hybrid retrieval: vector similarity (pgvector or AI Search) + full-text search
- RAG prompt with provenance metadata for attribution

### Step 6: UI
- Blazor Web App with chat interface and attributed answers
- Browse-by-game, browse-by-document-type, browse-by-content-category views

---

## 6. Cost estimate

| Component | Estimate |
|-----------|----------|
| PostgreSQL Flexible Server (Burstable B1ms) | ~$13/mo |
| Storage (5–15 GB pinball PDFs) | ~$1–2/mo |
| AI Services — embeddings (one-time, ~800 docs with text-embedding-3-large) | ~$5 one-time |
| AI Services — chat completions | per-query, usage-based |
| AI Search Basic (optional, only if not using pgvector) | ~$75/mo |
| App Service Plan B1 | ~$13/mo |
| Key Vault, Log Analytics, App Insights | <$5/mo combined |
| **Estimated baseline (pgvector backend)** | **~$32/mo + LLM usage** |
| **Estimated baseline (AI Search backend)** | **~$107/mo + LLM usage** |

The pgvector backend is the default for cost reasons; AI Search is available as a feature-flag for richer ranking if needed later.

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
