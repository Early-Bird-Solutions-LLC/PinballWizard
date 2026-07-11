# Design — Deterministic zero-content short-circuit for machine-scoped Wizard asks

**Date:** 2026-07-07
**Status:** Approved (design) — implementation plan to follow
**Related ADR:** 0052 (Deterministic zero-content short-circuit in the Wizard ask pipeline)

## Problem

On a machine detail page (`/machines/{manufacturerKey}/{opdbId}`) the primary
call-to-action is **"Ask the Wizard about this machine."** Clicking it navigates
to `/wizard?q=tell me about {title}` and auto-submits, which — on a cold semantic
cache (every *first* ask for a given machine) — runs a full Microsoft Foundry
agent turn: `getMachineByTitle` → `searchCorpus` → sub-agent dispatch → post-agent
guardrails.

For the large OPDB long tail — catalog entries from manufacturers we do not hold
first-party data on (e.g. *Super Flipp*, A.A. Amusements, 1987) — that turn can
only ever end one way: `searchCorpus` returns nothing, the agent refuses per its
prompt, the `NoCitation` guardrail fires
(`AiRouter.ApplyPostAgentGuardrailsAsync`), and `RefusalRecoveryService`
deterministically routes the user to community resources (`[Forums,
MachineReference]`). The expensive agent turn exists only to *rediscover* a
foregone conclusion, at token + latency cost, and it depends on the model
correctly refusing rather than hallucinating specs from parametric memory — a
no-masking / provenance-sacred risk on a customer-facing showcase.

## Why the obvious cheap signal is wrong

The detail page already computes `_docs.Count == 0` (it renders "No documents
linked to this machine"). That count is **not** a safe proxy for "the Wizard has
nothing to say," because it reads only the `scraped_documents` container (linked
PDFs), whereas the RAG index (`pinwiz-rag-v1`) is a **superset**. Synthesized
chunks are indexed under a machine's `machine_id` with **no** `scraped_documents`
row by design — metadata cards (`meta_{machineId}`), game overviews
(`overview_{machineId}`), and matched Kineticist / TiltForums rulesheets. The RAG
garbage collector documents this explicitly
(`RagIndexGarbageCollector` — synthesized classes "have NO scraped_documents row
by design"). Almost every supported-manufacturer machine has a metadata card, so
the Wizard *can* ground a real answer (year, designer, theme, MSRP) even when the
page shows zero linked documents.

**Gating on doc-link count would suppress exactly the answers we want to keep.**

## The safe signal

A direct **count** against the AI Search index, scoped to the machine:
`filter = machine_id eq '{id}'`, `Size = 0`, `IncludeTotalCount = true`. This is
the same pattern `CosmosAiSearchRagReconciler.CountChunksAsync` already uses. It
is cheap (no LLM, no embedding — a filtered count), and it reads the *same* store
the retriever reads, so:

- **0 chunks** → the Wizard genuinely has nothing for this machine → skip the
  agent, return the identical community-resource recovery.
- **≥ 1 chunk** → grounded content exists (at minimum a metadata card) → run the
  agent exactly as today.

## Scope decisions (settled during brainstorming)

1. **Gate location: server-side in `AiRouter`.** One authoritative gate, not
   duplicated in the UI. It is the backstop for every entry point that supplies a
   machine id.
2. **Machine identity: explicit `machineId` only.** Machine-scoped entry points
   (the detail-page button, and any future "ask about this machine" affordance)
   pass the id. The router gates **only** when the id is present. Typed free-text
   questions carry no id and flow through the agent unchanged — the agent already
   refuses + routes-to-community correctly on a genuinely empty machine, just at
   full token cost, which we accept. This choice has **zero false-skip risk**: we
   never gate a question we cannot pin to a single machine.
3. **Output parity, not a new UX.** The short-circuit reproduces the *existing*
   `NoCitation` outcome via
   `RefusalRecoveryService.BuildRecoveryAsync(question, RefusalCategory.NoCitation,
   ct)`. Same refusal panel, same community cards — instant and free instead of a
   full agent turn.

## Components

| Component | Change |
| --- | --- |
| `IMachineCorpusCoverage` (new Application port) | `Task<int> CountAsync(string machineId, CancellationToken ct)`. Defined in Application; keeps the router free of Infrastructure refs (Clean Architecture). |
| `AiSearchRagRetriever` (Infrastructure) | Implements the port; builds the filter with the **same** `BuildFilter(new RetrievalOptions { MachineId = id })` the real retrieval path uses, then issues a `Size=0, IncludeTotalCount=true` search and returns `TotalCount`. |
| `AiRouter.AnswerStreamingAsync` | After the semantic-cache miss and before the agent call: if `request.MachineId` is present and `CountAsync == 0`, emit the deterministic recovery as the `Final` chunk and return. Otherwise proceed to the agent. |
| Ask-stream request DTO / `WizardAskStreamEndpoint` | Add optional `MachineId`. |
| `IWizardStreamingClient` → `WizardAnswerStream` → `Wizard.razor` | Thread `machineId` through; `Wizard.razor` reads it via `[SupplyParameterFromQuery(Name = "machineId")]`. |
| `Shared/MachineDetail.razor` `OnAskWizardClick` | Append `&machineId={_machine.Id}` to the `/wizard` navigation. Passes `_machine.Id` (the identity the retriever filters on), not the URL slug — **plan-time verification:** confirm the AI Search `machine_id` field equals `Machine.Id`. |

## Data flow

```
button (q + machineId=_machine.Id)
  → /wizard?q=…&machineId=…
  → WizardAnswerStream auto-submit
  → POST /api/wizard/ask:stream { question, machineId }
  → AiRouter.AnswerStreamingAsync:
        semantic-cache check
        └─ miss → machineId present?
                    └─ yes → CountAsync(machineId) == 0 ?
                                ├─ yes → deterministic NoCitation recovery (NO LLM) ── return
                                └─ no  → agent turn (as today)
                    └─ no  → agent turn (as today)
```

## Observability + validation

- **Counter** `PinballWizardTelemetry.AiMachineScopeGateShortCircuits`, tagged
  `manufacturer` and `had_doc_links` (bool). Gives the firing rate broken out by
  manufacturer — a spike for a *supported* manufacturer is a leading indicator of
  an upstream ingestion gap, not a gate defect.
- **Structured Info log** per fire: `machineId`, `title`, `manufacturer` — a
  per-occurrence audit trail.
- **Filter-parity contract test.** Asserts the coverage-count filter and the
  retrieval filter emit a byte-identical `machine_id` clause (both from
  `BuildFilter`). This makes a false "no data" from code drift **structurally
  impossible**, not merely monitored.

## Testing (behavior, not structure)

- **Gate fires:** fixture machine with 0 indexed chunks → assert the Foundry agent
  client is **never invoked** *and* the community-resource recovery is returned.
- **Gate does not fire:** fixture machine with ≥ 1 chunk (e.g. metadata-card-only)
  → assert the agent **is** invoked. This is the regression guard for the exact
  hazard — never suppress a metadata-grounded answer.
- **Filter parity:** the contract test above.
- **Counter emission:** via the established `MeterListener` test pattern.

## Alternatives considered (rejected)

- **Gate on the page's doc-link count.** Unsafe — misses synthesized chunks;
  would suppress metadata-grounded answers. This is the core reason the design
  pivoted.
- **UI-only gate on the detail page.** Cannot be the authoritative backstop and
  duplicates decision logic into the Web layer.
- **Server-side resolution of free-text questions.** Would broaden token savings
  but reintroduces false-skip risk on ambiguous / multi-machine questions
  ("compare X and Y") — the exact "no answer when we have info" hazard the user
  called out.

## Out of scope (YAGNI — revisit if the counter shows volume)

- Gating typed free-text questions.
- A scheduled sampled audit job (run the real retriever against gated machines and
  alert on any that return content). The filter-parity test covers code drift;
  the runtime-discrepancy audit is a fast-follow if the rate justifies it.
- Precomputing per-machine chunk counts into `catalog_stats` so the detail-page
  button could reflect coverage at render time without a round-trip.
