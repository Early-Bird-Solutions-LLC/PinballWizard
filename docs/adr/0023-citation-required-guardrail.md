# 0023 — Citation-required guardrail: refuse when no citation can be attached

**Status:** Accepted
**Date:** 2026-05-07

## Context

[`vision.md`](../vision.md) and [`guardrails.md`](../guardrails.md)
goal #5 (provenance is sacred) frame *"every Wizard answer cites
a source"* as the architectural promise. Phase 3 implemented this
as prompt instruction (*"Always cite your sources"* in the
`Wizard.md` / sub-agent prompts) — which is unenforceable. Models
comply or not depending on prompt strength, post-processing
behavior, and the question shape. The Phase 3 post-close audit
(PR #94) caught the resulting README/vision.md overclaim and
softened the language to "refuse rather than fabricate" pending a
structural mechanism.

[ADR-0022](0022-citation-extraction.md) provides the structural
*input*: citations come from tool-call results, not prose. ADR-0023
provides the structural *enforcement*: when zero citations attach,
refuse rather than answer.

## Decision

After agent response and citation extraction, before returning
`WizardAnswer` to the caller:

```csharp
if (citations.Count == 0)
{
    return Refusal(RefusalCategory.NoCitation, "...");
}
```

### `NoCitation` is a new refusal category

`RefusalCategory` extends from the 5 categories defined in
[ADR-0017](0017-confidence-threshold-refusal.md) /
[ADR-0015](0015-cost-routing-and-semantic-cache.md) to **6**:

- `InsufficientGrounding` — retrieval returned chunks but their
  similarity was below the confidence threshold
- `OutOfScope` — Wizard agent dispatched to "unknown" branch
- `LowModelConfidence` — model self-reported confidence below
  floor
- `CostCeilingHit` — per-call cost ceiling tripped
- `HarmfulContent` — Foundry content safety blocked the response
- **`NoCitation`** *(new)* — no citation could be attached at all
  (typically because the agent answered without calling a
  grounding tool)

`InsufficientGrounding` and `NoCitation` are distinct on purpose.
A spike in one vs. the other tells different stories in
production:

- `InsufficientGrounding` ↑ ⇒ retrieval is degraded; chunks are
  being returned but the agent doesn't trust them. Usually a
  RAG-quality regression.
- `NoCitation` ↑ ⇒ tool-call-trace extraction is missing tool
  calls, OR agents aren't calling grounding tools at all. Usually
  a prompt regression OR a tool-call wiring regression.

### Threshold: structural, binary

ANY citation (≥1) passes the gate. v1 keeps the gate **binary**
— it does not require N citations or per-claim citations. The
entire architectural promise is that an answer is grounded *at
all*; layering "how grounded" comes later.

### Calibration

H3 baseline (build-spec.md § Phase 4 scope item 24) measures
`over_eager_refusal_rate{category=no_citation}` against the eval
set. Target: ≤ 20% (matches [ADR-0017](0017-confidence-threshold-refusal.md)'s
over-eager-refusal target).

If the rate exceeds 20%, the citation extractor's logic widens
(never tighten the binary gate without an ADR successor):

- **Option A:** Accept retrieved chunks as implicit citations
  even if not directly tool-traced (i.e., if `searchCorpus`
  returned chunks for the turn, those chunks count as citations
  even if the agent didn't echo them — the retrieval-set
  bookkeeping per [ADR-0022](0022-citation-extraction.md) makes
  this trivial).
- **Option B:** Re-prompt the agent with a "you must cite a
  source" reminder before the second-attempt response.

Both options are *loosenings*. The binary gate (`citations.Count
≥ 1`) is the architectural floor; v1.x calibration may relax it
toward "≥1 retrieved chunk in the turn's retrieval set" but
never tightens it without an explicit ADR successor.

### Order of refusal evaluation

Per [ADR-0017](0017-confidence-threshold-refusal.md), the
confidence threshold check runs first; the citation-required check
runs second. A turn can refuse with EITHER `InsufficientGrounding`
(low confidence) OR `NoCitation` (no citation). If both apply,
`InsufficientGrounding` wins (it's more specific — confidence is
low because *retrieval* is bad; the absent citation is the
symptom, not the cause).

### Pairs with [ADR-0022](0022-citation-extraction.md)

The citation-required guardrail only works because citations are
structural. With the Phase 3 regex extractor, this guardrail
would have produced absurd refusals on correct answers where the
agent didn't echo the URL. ADR-0022 makes the input reliable;
ADR-0023 makes the gate enforceable.

## Consequences

**Positive:**

- *"Every answer cites a source, or refuses"* becomes
  **structurally true**. README and vision.md stop overclaiming
  per Phase 3 close audit findings.
- The guardrail is auditable: refusal path is a single line of
  code; reviewers see the policy in one place.
- Distinct refusal category (`NoCitation`) enables prod debugging
  — distinct root causes from `InsufficientGrounding`.
- Reinforces [ADR-0014](0014-microsoft-foundry-orchestration.md)'s
  agents-must-call-grounding-tools posture. An agent that
  short-circuits to "I know this from training" is structurally
  refused, not silently allowed.
- Consistent with `guardrails.md` goal #5 "provenance is
  sacred" — the ADR makes the goal mechanically enforced.

**Negative:**

- **Aggressive in the early days.** A high-quality answer the
  agent generated by reasoning over multiple grounded retrievals
  could refuse if the citations don't surface through the
  extractor (e.g., agent paraphrased rather than tool-called). H3
  calibration measures the magnitude.
- **Edge case: questions with no factual answer** ("What's the
  best pinball machine ever made?") refuse correctly with
  `NoCitation`. This is correct per the showcase posture — the
  Wizard isn't a chatty assistant — but some users will find it
  terse. UX text on top of `NoCitation` is Phase 5 (UI).
- **Tool-call failures look identical to "agent didn't call the
  tool"** in current telemetry. If `searchCorpus` errors
  internally, the trace shows zero successful tool results, and
  the gate refuses with `NoCitation`. Correct posture (no
  citation = refuse) but indistinguishable from agent behavior.
  **Mitigation:** build-spec § Phase 4 scope item 25
  (observability.md update) adds a `pinwiz.ai.tool_errors_total`
  counter tagged with `tool=searchCorpus|getMachineByTitle` so
  the two failure modes are distinguishable in production. A
  spike in `pinwiz.ai.refusals_total{category=no_citation}`
  correlated with `pinwiz.ai.tool_errors_total` indicates tool
  errors; a spike with no correlation indicates agent-doesn't-
  call-tool. Revisit if the correlation is noisy or the
  distinction is needed at finer granularity.
- **Increases refusal rate at H2 / H3 vs. Phase 3 H2 baseline.**
  Expected; the H2 baseline (`citation_precision = 0.133`) was
  artificially propped up by regex acceptance of hallucinated
  URLs. The new floor is honest.

## Alternatives considered

- **Soft enforcement** (warn / log but don't refuse). Rejected —
  vision.md "refuse rather than fabricate" requires hard refusal;
  soft enforcement is the failure mode the ADR exists to prevent.
- **Allow uncited answers if confidence is high.** Rejected —
  the failure mode (high-confidence fabrication of well-known but
  unreferenceable claims) is exactly what this ADR prevents.
- **Threshold ≥2 citations for safety-critical questions.**
  Deferred — v1 gate is binary; per-question-class
  differentiation is Phase 5+.
- **Apply only to public Wizard, not CLI / eval harness.**
  Rejected — the architectural invariant must hold across all
  entry points; CLI / eval ARE part of the showcase.
- **Combine `InsufficientGrounding` and `NoCitation` into one
  category.** Rejected — production debugging needs the cause,
  not just the fact.
- **Keep the gate as a prompt instruction only.** Rejected — this
  is the failed Phase 3 approach. Prompt instructions are
  best-effort; ADR-0023 makes the invariant structural.

## References

- [ADR-0014](0014-microsoft-foundry-orchestration.md) —
  agents-must-call-grounding-tools posture
- [ADR-0017](0017-confidence-threshold-refusal.md) — shared
  refusal-category surface and over-eager-refusal target
- [ADR-0022](0022-citation-extraction.md) — provides the
  structural citation input this guardrail enforces
- [build-spec.md § Phase 4](../build-spec.md) — scope items 5
  (this ADR), 23 (citation-required guardrail implementation),
  24 (H3 final eval + threshold calibration)
- [guardrails.md](../guardrails.md) goal #5 — provenance / refusal
  invariant
- [vision.md](../vision.md) — "refuse rather than fabricate"
  framing
- Phase 3 close audit (PR #94) — the README/vision.md overclaim
  that motivated structural enforcement
