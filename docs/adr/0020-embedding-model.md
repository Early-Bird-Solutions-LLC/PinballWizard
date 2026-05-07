# 0020 — Embedding model: text-embedding-3-large @ 3072d

**Status:** Accepted
**Date:** 2026-05-07

## Context

[ADR-0014](0014-microsoft-foundry-orchestration.md) drafted
`text-embedding-3-large` @ 3072 dimensions as the embedding model
for Phase 4 RAG. Phase 4's design conversation (2026-05-07)
re-validated the choice against PdfPig output dimensions, AI
Search Basic vector field constraints, and the curated-subset
cost projection. This ADR formalizes the confirmation as an
immutable record so future re-evaluation has a checkpoint and so
the choice is not relitigated mid-phase.

## Decision

`text-embedding-3-large` @ **3072 dimensions** for all Phase 4 RAG
embeddings — both PDF chunks and synthesized metadata cards. Same
model for query and corpus.

Pinned via the Foundry deployment name `text-embedding-3-large`
already provisioned in Phase 3 H1
(`pinwiz-foundry-dev-hlpz4/pinwiz-wizard`).

### Rationale

- **AI Search Basic supports vector fields up to 15K dims** —
  3072 is well within. No infra constraint.
- **3072d gives stronger semantic discrimination** than 1536d
  (`text-embedding-3-small`) on benchmark retrieval tasks (MTEB).
  For document-grounded Q&A on technical manuals, retrieval
  quality compounds across the showcase narrative — worth the
  storage cost.
- **Cost: ~$0.13 / 1M tokens.** Curated-subset projection: ~7
  manuals × ~150 pages × ~500 tokens/page = ~525K tokens →
  ~$0.07 first run. Affordable; well under the $5 per-run ceiling
  in build-spec.md § Phase 4 P4-R3.
- **Same model for query and corpus** avoids the dual-model
  asymmetry that hurts retrieval.

### Index sizing

3072 floats × 4 bytes = ~12 KB per chunk vector. For the curated
subset (estimate 5,000–10,000 chunks across 7 machines), the
vector field consumes ~60–120 MB. AI Search Basic index limit is
**2 GB total per index**; well within budget for v1.

### Phase 4.5 cost note

Full corpus expansion is ~50–100× the curated subset's chunk
count. Vector storage scales linearly: ~3–12 GB. **This may
exceed AI Search Basic's 2 GB single-index limit**, in which
case Phase 4.5 chooses one of:

- Switch to `text-embedding-3-small` @ 1536d (4 KB/chunk → 1–4 GB)
- Use Matryoshka truncation on `text-embedding-3-large` (e.g.,
  truncate to 1024d) — preserves model quality with smaller
  dimensions
- Upgrade to AI Search Standard SKU
- Shard across multiple `pinwiz-rag-vN` indexes by manufacturer

Phase 4.5 owns the decision; Phase 4 records the trip-wire here.

## Consequences

**Positive:**

- Zero new dependency vs. ADR-0014's draft choice; the deployment
  is already live.
- Pre-flight cost budget verified against the curated subset; runs
  comfortably under ceiling.
- Same model for query and corpus = no asymmetry to manage.
- 3072d retrieval quality is the showcase differentiator over a
  1536d "good enough" alternative.

**Negative:**

- **Hard tokenizer pin** to `cl100k_base` BPE
  ([Microsoft.ML.Tokenizers](https://www.nuget.org/packages/Microsoft.ML.Tokenizers)).
  If OpenAI ships a new embedding model with a different
  tokenizer, [ADR-0019](0019-hybrid-chunking.md)'s chunker has to
  re-evaluate. The risk is small (cl100k_base is shared across
  GPT-4-class models) but recorded.
- **Phase 4.5 trip-wire on storage.** Documented above; not a
  Phase 4 problem but a known Phase 4.5 fork.
- 3072d is "expensive" in vector storage vs. 1536d alternatives.
  Acceptable for showcase; recorded for cost-discipline review at
  H3.

## Alternatives considered

- **`text-embedding-3-small` @ 1536d.** Rejected for Phase 4 —
  retrieval quality is materially worse on technical-manual
  benchmarks. Revisit at Phase 4.5 scale if storage limits force
  a change.
- **Sentence-transformers (run locally).** Rejected — we're on
  the Azure-managed Foundry path; no local embedding infra; would
  contradict ADR-0014.
- **Custom fine-tuned embedding** (e.g., domain-tuned on
  pinball-specific corpus). Rejected per Phase 2 architecture
  lock (deferred features index): ~2% recall improvement
  estimated, not worth the engineering investment for v1.
- **`text-embedding-3-large` @ 1024d via Matryoshka truncation.**
  Rejected for v1 — adds complexity (post-process truncate, then
  L2-normalize); revisit at Phase 4.5 cost-reduction conversation
  if needed.
- **Cohere Embed-English-v3.** Rejected — adds a non-Azure
  dependency; contradicts the Foundry-native showcase posture.
- **Retain ADR-0014's draft as-is without re-validating.**
  Rejected — Phase 4 design conversation is the right gate for
  re-confirming model choices against the new infrastructure
  constraints (AI Search SKU, index size projections, tokenizer
  alignment).

## References

- [ADR-0014](0014-microsoft-foundry-orchestration.md) — original
  draft choice
- [ADR-0019](0019-hybrid-chunking.md) — chunking depends on the
  tokenizer alignment
- [ADR-0021](0021-ai-search-index-schema.md) — vector field
  configuration and schema budget
- [build-spec.md § Phase 4](../build-spec.md) — scope items 2, 15;
  cost projection P4-R3
- [build-spec.md § Phase 4.5](../build-spec.md) — full-corpus
  scale considerations
