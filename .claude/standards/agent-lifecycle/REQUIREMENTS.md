# agent-lifecycle — requirements index

| ID | slug | WHEN (summary) | SEV | REF |
|---|---|---|---|---|
| AGENT-01 | classify-before-coding | adding a new LLM agent | 🔴 | INVARIANTS#19 · ADR-0051 · ADR-0064 · ADR-0066 |
| AGENT-02 | per-agent-adr-before-enablement | introducing a named agent | 🔴 | INVARIANTS#19 · ADR-0066 |
| AGENT-03 | versioned-prompt-as-code | authoring/changing agent prompts | 🔴 | INVARIANTS#12 · ADR-0018 · ADR-0066 · RAG-05 |
| AGENT-04 | runs-persist-with-outcomes-or-dated-transitional | agent pipeline producing diagnoses/reviews/remediations | 🔴 | INVARIANTS#19 · ADR-0066 |
| AGENT-05 | human-review-before-kb-write | agent writes to KB / memory / lessons | 🔴 | INVARIANTS#19 · ADR-0066 |
| AGENT-06 | eval-before-self-improving-claim | claiming improvement or adding learning loop | 🔴 | INVARIANTS#19 · ADR-0016 · ADR-0066 · OBS-03 |
| AGENT-07 | cost-and-tool-bounds | adding or enabling an agent | 🔴 | INVARIANTS#10 · ADR-0015 · ADR-0066 |
| AGENT-08 | adversary-review-before-enablement | flipping enablement to true | 🔴 | INVARIANTS#19 · ADR-0066 |
| AGENT-09 | ci-comment-only-default | CI agent posting on issues/PRs | 🔴 | INVARIANTS#19 · ADR-0041 · ADR-0066 |
| AGENT-10 | deterministic-act-gate | Act tier or mutate/dispatch from agent output | 🔴 | INVARIANTS#19 · ADR-0066 |
| AGENT-11 | run-store-authz-redaction-ttl | persisting or exposing agent run records | 🔴 | INVARIANTS#19 · ADR-0066 · OBS-03 |
| AGENT-12 | idempotent-bounded-ingress | webhook/queue/event ingress | 🔴 | INVARIANTS#19 · ADR-0066 |
| AGENT-13 | kb-retrieval-isolation | retrieving lessons into model context | 🔴 | INVARIANTS#19 · ADR-0066 |
