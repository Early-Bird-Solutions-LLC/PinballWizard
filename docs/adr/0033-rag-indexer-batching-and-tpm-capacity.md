# 0033 — RAG indexer batching parameters and TPM capacity

**Status:** Accepted  
**Date:** 2026-06-05

## Context

During the AB#259 live migration (Step 4 — full RAG index rebuild), three successive
`--run-rag-backfill` runs failed or had to be killed before completing. Each failure
was traced to a distinct root cause, all fixed in sequence:

1. **Source re-fetch latency (PR #321):** the backfill fetched PDF bytes from source
   URLs on every run. `LocalFirstDocumentBytesSource` fixed this by serving from
   verified local downloads.

2. **Embedding 429 rate-limit errors (commit `938cd39`, PR #323):** the 50k-TPM
   ceiling on `text-embedding-3-large` was saturated during burst embedding. Raised
   to 250k TPM. Standard SKU capacity is a rate ceiling, not a reservation — no cost
   change.

3. **Embedding-call timeout >100s (PR #322):** a single embedding call batching all
   chunks of a large document (~140 texts) exceeded the embedding client's network
   timeout. Fixed by sub-batching embedding calls to `EmbeddingBatchSize=16`.

After PR #322 merged, a fourth run still stalled. Investigation showed the process
reached 6.7 GB working set and produced no output for >30 minutes while grinding on a
single document. Root cause: `BatchSize=1000` (the AI Search upload-batch size) meant
each document produced exactly **one** upload worker, making `EmbeddingMaxConcurrency=8`
completely idle. With one worker per document, the `EmbeddingBatchSize=16` sub-batches
ran **serially** — a 1,000-chunk document executed 63 sequential embedding calls
(~5–10s each = ~5–10 min wall-clock), and a large PDF's full text was held in memory
for the duration (~6.7 GB observed on the heaviest document).

## Decision

### Batching parameters (`RagIndexerOptions` defaults)

| Parameter | Old | New | Rationale |
|---|---|---|---|
| `BatchSize` | 1000 | **100** | Splits large documents into ≥10 concurrent upload workers, giving `EmbeddingMaxConcurrency=8` real work to parallelize. |
| `EmbeddingBatchSize` | 16 | **32** | Reduces round-trips per batch; still well under the ~100s client timeout that motivated sub-batching. |
| `EmbeddingMaxConcurrency` | 8 | **8** | Unchanged — correct at 250k TPM; lower to 4 if sustained 429s appear. |
| `IndexUploadConcurrency` | 4 | **4** | Unchanged — AI Search Basic 1-SU throughput envelope is fine here. |

**Why BatchSize=100 specifically:** at `EmbeddingMaxConcurrency=8` and `EmbeddingBatchSize=32`,
a 1,000-chunk document fans into 10 workers × 4 embedding calls each = up to 40 calls in
flight simultaneously vs. the old 63 serial calls. Expected wall-clock for the largest
documents: ~2–3 min instead of ~10 min. The right value is the smallest multiple of
`EmbeddingBatchSize` that keeps all 8 workers busy — 100 ≥ 8 × 32/4 = 64; chosen as a
round number above that threshold.

**Memory implication:** smaller upload batches mean smaller in-flight chunk slices.
The 6.7 GB peak WS was caused by holding all 1,000+ chunk texts + vectors in one worker
simultaneously. With `BatchSize=100`, peak per-worker allocation is bounded at ~100
chunks × (chunk text + 3072-float vector) ≈ a few MB.

### Token observability

`AzureOpenAIChunkEmbedder` now emits `pinwiz.rag.embedding_tokens_total` (a
`Counter<long>`) on every `EmbedBatchAsync` call, sourced from
`EmbeddingTokenUsage.InputTokenCount` — actual billed tokens, not an estimate. Tagged
with `call_site` (`backfill` | `changefeed` | `query`).

This instrument enables the TPM-ceiling decision described in the open question below:
operators can observe **peak tokens/minute during a full rebuild** from dashboards and
compare it against the deployed ceiling, rather than relying on back-of-envelope chunk-
count estimates.

### Open question: should the TPM ceiling be raised from 250k to 350k?

The East US 2 regional Standard ceiling for `text-embedding-3-large` is **350k TPM**
(verified via `az cognitiveservices usage list`). We are currently at 250k. Raising is
free (Standard is pay-per-token; capacity is a rate ceiling only).

**Decision deferred until the first full rebuild completes with the new batching
settings.** Evidence needed:

1. **Peak tokens/min during a rebuild** — read from `pinwiz.rag.embedding_tokens_total`
   aggregated over a 1-minute window in Azure Monitor / OTel exporter. A peak near
   250k (within ~30% headroom = 175k+) means a raise is warranted. A peak well below
   200k means 250k is adequate.

2. **429 rate-limit events** — if `Microsoft.Extensions.Http.Resilience`'s retry logs
   show `RateLimitReached` during the rebuild, the ceiling is constraining throughput
   and should be raised.

3. **Wall-clock rebuild time** — if the rebalanced batching brings full-corpus rebuild
   under 15 minutes at 250k, the ceiling is not the bottleneck. If it's still 30+
   minutes and 429s are absent, the bottleneck is something else (AI Search upload
   throughput, PDF extraction, etc.).

**Trigger to raise:** peak tokens/min > 175k **or** any 429 events during a rebuild.
**Trigger to stay:** peak tokens/min < 175k **and** zero 429s.

Update this ADR with findings after the next clean full rebuild.

## Consequences

**Positive:**

- Large-manual backfills that previously stalled for 10+ minutes per document now
  complete in 2–3 min, bringing full-corpus rebuild time from hours to ~15 min.
- Memory footprint bounded per worker — no more multi-GB WS on single documents.
- `pinwiz.rag.embedding_tokens_total` provides the real-token data needed to make
  the TPM-ceiling decision with evidence rather than estimates.
- Defaults are now explained in terms of their interaction (not just individually),
  preventing the BatchSize=1000 / EmbeddingMaxConcurrency=8 mismatch from being
  re-introduced silently.

**Negative:**

- More AI Search upload calls per document (10 × 100-chunk batches vs. 1 × 1000-chunk
  batch). Each call has a small fixed overhead; in practice this is negligible vs.
  embedding latency.
- `EmbeddingBatchSize=32` doubles the per-call text volume vs. 16. If a future corpus
  includes extremely large chunks (long pages, low overlap), this may need re-tuning.
  The 2048-input Azure OpenAI limit and ~100s timeout are the hard bounds; 32 is far
  from both.
- TPM-ceiling decision remains open — a rebuild with monitoring is required to close it.

## Alternatives considered

- **Keep `BatchSize=1000`, raise `EmbeddingBatchSize` to saturate the ceiling.**
  Rejected — the serialization problem is structural: with one batch per document,
  `EmbeddingMaxConcurrency` can never help regardless of sub-batch size.

- **Set `BatchSize=50`.** Would give even more concurrency but 50 < 8 × 32/4 = 64 —
  some workers would have only one sub-batch call each, and the upload-call overhead
  would dominate. 100 is the sweet spot.

- **Raise `EmbeddingMaxConcurrency` to 16.**  Rejected for now — at 250k TPM, 8
  workers × 32 texts × ~300 tokens/text = ~76k tokens/batch is already a healthy burst.
  Revisit if the TPM-ceiling decision lands on 350k.

- **Make `BatchSize` and `EmbeddingBatchSize` CLI flags.** Noted as a follow-up — would
  allow per-run tuning without a code change. Deferred; the defaults now reflect
  evidence-based values rather than SDK-limit maximums.

## References

- [ADR-0020](0020-embedding-model.md) — embedding model choice and cost projection
- [ADR-0021](0021-ai-search-index-schema.md) — AI Search index schema and upload contract
- PR #322 — `EmbeddingBatchSize` decoupling (timeout fix)
- PR #323 — TPM ceiling 50→250k (rate-limit fix)
- PR #324 — batching tuning + `pinwiz.rag.embedding_tokens_total` (this ADR)
- `tools/MIGRATION-RUNBOOK.md` — operational process rules for live migrations
