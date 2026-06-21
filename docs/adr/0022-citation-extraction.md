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
   Phase 4](../build-spec.md) scope item 21): if the tool
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
§ Phase 4](../build-spec.md) scope item 10. Existing tests against
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

## Follow-up 2026-06-10 — case-insensitive binding + both OPDB URL schemes

The JsonElement dispatch arms probed PascalCase property names
("Hits", "OpdbId"), but AIFunctionFactory serializes function results
camelCase ("hits", "opdbId") — verified live against gpt-4o. The
structured arms therefore never fired on the real Foundry path; every
citation rode the regex fallback over raw tool-result JSON, which
matched only `https://opdb.org/machines/{id}`. When the opdbSourceUrl
migration (PR #339 + tools/migrate-opdb-source-urls.csx) replaced
those URLs with `/search?q={id}` deep links, extraction collapsed to
zero citations and the deployed site refused 100% of questions for
~2.5 hours (eval citation_precision 0.967 → 0.111, 30/30 refusals).

Changes: property probing and deserialization are case-insensitive
(JsonSerializerDefaults.Web; PascalCase still accepted so typed unit
fixtures keep working); the URL regex accepts both `/machines/{id}`
and `/search?q={id}`; structured-binding JsonException degrades to the
regex fallback instead of propagating (the extractor runs outside the
router's try/catch). RegexLegacyCitationExtractor received the same
regex widening so the `source=regex_legacy` cutover telemetry
comparison stays meaningful. Live-shape regression tests (camelCase
fixtures) now pin the runtime JSON casing — the original JsonElement
tests serialized fixtures PascalCase, which is why they stayed green
through the outage.

## Follow-up 2026-06-20 — inline-token contract + `ExtractWithSourceIndex` + reconciliation drop rule

The inline citation marker feature extends the ADR-0022 extraction surface
with three additions:

- **`[[cite:k]]` inline-token contract.** The Wizard prompt (v5.2026.06)
  instructs the model to emit `[[cite:k]]` tokens (k = 1-based ordinal of
  the `searchCorpus` source in tool-trace return order) at grounded
  sentences in its answer prose. Sub-agent prompts (Repair, Rules,
  Valuation) echo the same `[[cite:k]]` at grounded sentences before
  returning their text to the Wizard. This is the structural citation
  mechanism the original ADR considered and deferred as "citation-emitting
  tool (Alternatives considered)"; the token form avoids tool-call overhead
  by piggybacking on the prose that the model generates anyway.
- **`ExtractWithSourceIndex` k→SourceUrl index.** `ToolTraceCitationExtractor`
  gains a new public surface:
  `(IReadOnlyList<Citation> Citations, IReadOnlyList<string> SourceIndex) ExtractWithSourceIndex(AgentResponse?)`.
  `SourceIndex[k-1]` is the `DocumentUrl` of the k-th `searchCorpus` hit
  in tool-trace order — exactly what the reconciler needs to resolve
  `[[cite:k]]` → card ordinal N. Only `searchCorpus` hits populate
  `SourceIndex`; `getMachineByTitle` results and OPDB-regex citations from
  sub-agent text go into `Citations` only (they are grounding records, not
  numbered sources the model cites with `[[cite:k]]`). Hits with a blank
  `DocumentUrl` are skipped (they would produce no citation either); the
  k-numbering stays consistent with what the model saw.
- **Reconciliation drop-on-no-match rule (OBS-01 enforcement).**
  `InlineCitationReconciler.Reconcile` walks the final `AnswerText`,
  looks up each `[[cite:k]]` token in `SourceIndex` to find the
  `SourceUrl`, then resolves that URL to the card ordinal N in
  `WizardAnswer.Citations`. Tokens that fail either lookup (k out of range,
  URL not in the citation list) are **dropped** — replaced with the empty
  string — and counted in `AiInlineMarkerDropped`. They are **never
  rendered** as a bare `[[cite:k]]` and never fabricated as a phantom
  card (OBS-01: dropped markers are metered, not faked). Only markers
  that survive the full k→SourceUrl→N chain appear in the rewritten
  `AnswerText` as `[[cite:N]]`, which `MarkdownTokenizer` then renders
  as a CSP-safe `CitationMarker` superscript.

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
- [ADR-0026](0026-user-delight-frontend-and-streaming.md) § Follow-up
  2026-06-20 — the complementary follow-up describing the inline-marker
  layer from the citation-surface (§8) perspective, including the
  left-flipper round-trip and the `Final`-only resolution rule
- [build-spec.md § Phase 4](../build-spec.md) — scope items 4
  (this ADR), 10 (tool-trace extractor implementation), 21
  (`searchCorpus` tool that feeds citations)
- Phase 3 retrospective lesson 5 — the gap that motivated this
  ADR
