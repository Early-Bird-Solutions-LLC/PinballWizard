# 0022 — Tool-call-trace citation extraction (replaces regex over agent prose)

**Status:** Accepted
**Date:** 2026-05-07

## Context

Phase 3 PR 6 (citation extraction) used regex matching on agent
response prose to extract OPDB URLs and treat them as citations.
The H2 baseline run (2026-05-07) revealed two failure modes:

1. **False negatives.** The agent successfully called
   `getMachineByTitle` and got back grounded data, but didn't
   echo the URL into prose — so regex saw zero citations and the
   confidence-threshold gate ([ADR-0017](0017-confidence-threshold-refusal.md))
   over-flagged refusals because the `citation_coverage` signal
   went to zero. Combined with the connected-agents wiring gap
   (Phase 3 retrospective lesson 4), this drove H2's
   `citation_precision = 0.133`.
2. **False positives.** The agent could hallucinate
   plausible-looking URL fragments and regex would accept them as
   real citations. Phase 3 retrospective lesson 5 mandates a
   structural replacement.

The Phase 4 design conversation (2026-05-07) chose a structural
mechanism: tool-call trace inspection. Citations come from what
the agent *retrieved*, not from what it *said*.

## Decision

`ICitationExtractor` reads citations from the agent's tool-call
result trace, not from response prose.

### Algorithm

The extractor inspects `AgentResponse.ToolCallResults` (or the
SDK's equivalent surface in the pinned `Microsoft.Agents.AI`
version):

1. **For each call to `getMachineByTitle`:** if the tool returned
   a non-null `MachineGroundingDto` with non-null OPDB URL, that
   URL is a citation. Provenance: OPDB record retrieved during
   the turn.
2. **For each call to `searchCorpus`** (added in [build-spec.md §
   Phase 4](../build-spec.md) scope item 20): if the tool
   returned `RetrievedChunk[]` items with non-empty `document_url`
   + `page_start` / `page_end`, each chunk produces a citation.
   Multiple chunks from the same `document_id` collapse into a
   single citation with the union of page ranges.
3. **Citations are NOT extracted from agent prose.** The agent
   may still mention sources in its text — this is encouraged
   because users see prose, not tool traces — but the citation
   surface returned to the caller is *structural* and derives
   only from tool-call results.
4. **Empty tool-call trace ⇒ empty citations** ⇒ [ADR-0023](0023-citation-required-guardrail.md)
   citation-required guardrail fires (refusal with category
   `NoCitation`).

### Telemetry

`pinwiz.ai.citations.extracted_total` counter, tagged with
`source = tool_trace | regex_legacy` so the cutover is observable.
Once `regex_legacy` extractions drop to zero in production logs,
the legacy extractor can be removed.

### Migration

Phase 3's `OpdbUrlCitationExtractor` is retired in [build-spec.md
§ Phase 4](../build-spec.md) scope item 9. Existing tests against
it stay if they pass against the new impl; otherwise they're
deleted with a one-line note (no parallel-test farm). Citations
are the load-bearing artifact for `citation_precision` /
`citation_recall` evaluators — H2 baseline rerun measures the
impact of the swap directly.

## Consequences

**Positive:**

- Citations are structurally tied to what the agent actually
  retrieved, not to what it chose to mention. The "agent answers
  correctly but forgot to mention the URL" failure mode is
  eliminated.
- Hallucinated URLs in prose are no longer treated as real
  citations. The regex accept-anything failure mode is impossible.
- Page-anchor citations from `searchCorpus` work natively with
  the structural extractor — `RetrievedChunk` already carries
  `document_url` + `page_start` + `page_end` + `section_heading`
  per [ADR-0021](0021-ai-search-index-schema.md).
- The extractor's logic is auditable: a reviewer reads the
  citation policy in one method, not by reasoning over regex
  patterns.
- Pairs cleanly with [ADR-0023](0023-citation-required-guardrail.md):
  empty tool-call trace ⇒ empty citations ⇒ refusal. The two ADRs
  together make "every answer cites a source, or refuses"
  structurally true.

**Negative:**

- **Agents must call a grounding tool for any answer with
  citations.** A plausible-sounding answer the agent generates
  from training data alone has zero tool calls, zero citations,
  and refuses per [ADR-0023](0023-citation-required-guardrail.md).
  This is the safety invariant; it is a feature, not a regression
  — but the failure mode (refused training-data-only answer) is a
  visible cost recorded here.
- **Microsoft Agent Framework's tool-call trace surface is
  SDK-version-dependent.** If the surface changes, the extractor
  needs an update. Pinned package version + integration tests
  catch behavioral regressions; the abstraction
  (`ICitationExtractor`) localizes the change.
- **Cutover may show as increased refusal rate** during H2 because
  agents that previously got credit for prose-mentioned URLs now
  refuse if they didn't actually call a tool. This is *expected
  and correct* — the H2 number isn't a regression, it's the new
  honest baseline. Phase 3 retrospective lesson 4 (connected
  agents) will compensate by making more answers go through real
  tool calls.
- **Multiple chunks from the same document collapse** into one
  citation. A future "deep-dive citation" view (Phase 5+ UI) may
  want chunk-granular citations; that's a Phase 5 layering, not a
  Phase 4 schema change.

## Alternatives considered

- **Keep regex extraction, expand patterns to match more URLs.**
  Rejected — the underlying problem (citations should reflect
  retrieval, not prose) doesn't go away with bigger regex.
- **Citation-emitting tool** (require the agent to call
  `cite(url, span)` explicitly per claim). Rejected for v1 —
  depends on the agent reliably calling a metadata tool, which
  prompts can't enforce. Revisit in Phase 5+ if tool-call-trace
  inspection produces noise.
- **Combine prose-regex AND tool-trace** (union). Rejected —
  regex's false positives outweigh its catch rate; clean cutover
  beats hybrid; observability of the swap (`source` tag) requires
  a clear single-source semantic.
- **Structured-output / JSON-mode response with `citations`
  field.** Rejected for v1 — not all Agent Framework agent calls
  support strict JSON mode; tool-call trace is universal across
  agents.
- **Heuristic: any URL that appears in BOTH prose AND a tool-call
  trace.** Rejected — adds complexity for a marginal accuracy
  improvement; the structural signal alone is sufficient.
- **Defer until Foundry exposes a "citation" first-class
  primitive.** Rejected — no roadmap signal; Phase 4 needs
  citation extraction to ship.

## References

- [ADR-0014](0014-microsoft-foundry-orchestration.md) — Microsoft
  Agent Framework provides the tool-call trace primitive
- [ADR-0017](0017-confidence-threshold-refusal.md) — confidence
  refusal; `InsufficientGrounding` remains a distinct category
  from `NoCitation`
- [ADR-0021](0021-ai-search-index-schema.md) — `RetrievedChunk`
  schema feeds the citation surface
- [ADR-0023](0023-citation-required-guardrail.md) — pairs with
  this ADR for the structural "every answer cites" invariant
- [build-spec.md § Phase 4](../build-spec.md) — scope items 4, 9,
  20
- Phase 3 retrospective lesson 5 — the gap that motivated this
  ADR
