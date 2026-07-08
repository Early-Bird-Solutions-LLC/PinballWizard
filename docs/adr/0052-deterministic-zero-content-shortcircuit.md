# 0052 — Deterministic zero-content short-circuit in the Wizard ask pipeline

**Status:** Accepted
**Date:** 2026-07-07

## Context

The machine detail page's primary call-to-action, "Ask the Wizard about this
machine," navigates to `/wizard?q=tell me about {title}` and auto-submits. On a
cold semantic cache — every *first* ask for a machine — this runs a full
Microsoft Foundry agent turn (ADR-0014): `getMachineByTitle` → `searchCorpus` →
sub-agent → post-agent guardrails.

For the large OPDB long tail — catalog entries from manufacturers we hold no
first-party data on (e.g. *Super Flipp*, A.A. Amusements, 1987) — that turn has
exactly one possible ending: `searchCorpus` returns nothing, the agent refuses,
the `NoCitation` guardrail (ADR-0023) fires, and `RefusalRecoveryService` routes
the user to community resources per the ADR-0027 refusal-routing matrix
(`[Forums, MachineReference]`). The agent turn exists only to rediscover a
foregone conclusion, at token + latency cost, and it leans on the model refusing
correctly rather than hallucinating specs from parametric memory — a
no-fabrication risk (invariant #17) on a customer-facing showcase.

The tempting cheap signal — the `_docs.Count == 0` the page already renders as
"No documents linked to this machine" — is **unsafe**. That count reads only the
`scraped_documents` container (linked PDFs). The RAG index (`pinwiz-rag-v1`,
ADR-0021) is a **superset**: synthesized chunks are indexed under a machine's
`machine_id` with no `scraped_documents` row *by design* — metadata cards
(`meta_{machineId}`), game overviews (`overview_{machineId}`), and matched
Kineticist (ADR-0043) / Tilt Forums (ADR-0050) rulesheets. `RagIndexGarbageCollector`
documents this explicitly. Almost every supported-manufacturer machine has a
metadata card, so the Wizard can ground a real answer (year, designer, theme,
MSRP) even with zero linked documents. Gating on the doc-link count would suppress
exactly those answers.

## Decision

Add a deterministic preflight to `AiRouter.AnswerStreamingAsync`, after the
semantic-cache miss and before the agent turn, gated on an **explicit
`machineId`** supplied by machine-scoped entry points:

1. Count indexed chunks for the machine via a new Application port
   `IMachineCorpusCoverage.CountAsync`, implemented in `AiSearchRagRetriever` as a
   `machine_id eq '{id}'`, `Size=0`, `IncludeTotalCount=true` search that reuses
   the **same** `BuildFilter` the real retrieval path uses.
2. **Zero chunks** → reproduce the identical `NoCitation` recovery via
   `RefusalRecoveryService.BuildRecoveryAsync(question, RefusalCategory.NoCitation,
   ct)` and return — **no LLM call**.
3. **≥ 1 chunk** (or no `machineId`) → run the agent turn exactly as today.

The gate keys on chunk count, never on doc-link count. It fires only when a
question can be pinned to a single machine (the detail-page button passes
`Machine.Id`); typed free-text questions carry no id and are unchanged.

## Consequences

- The OPDB long-tail "ask" — a foregone refusal — costs zero tokens and returns
  instantly, with the identical community-resource UX. The token/latency win
  scales with how much of the catalog is uncovered (the majority of OPDB entries).
- It is **safe by construction against the "no answer when we do have info"
  failure**: gating on index chunk count (not doc-link count) means a
  metadata-card-only machine still routes to the agent. A behavior test asserts
  the agent *is* invoked for a machine with ≥ 1 chunk.
- A false "no data" could otherwise arise from the count filter drifting from the
  retrieval filter; both are built from the same `BuildFilter`, and a contract
  test asserts an identical `machine_id` clause — making that drift structurally
  impossible.
- The short-circuit is metered (`AiMachineScopeGateShortCircuits`, tagged
  `manufacturer` + `had_doc_links`) and logged per fire, so the firing rate is
  observable; a spike for a *supported* manufacturer surfaces an ingestion gap
  rather than hiding one (no-masking posture).
- Typed free-text questions about uncovered machines still pay the full agent cost
  — an accepted trade to avoid fuzzy server-side resolution and its false-skip
  risk. If the counter shows the volume justifies it, a later ADR can add
  free-text gating and/or a scheduled runtime-discrepancy audit.
- No data migration: no change to how `scraped_documents`, the index, or lookup
  rows are written.
