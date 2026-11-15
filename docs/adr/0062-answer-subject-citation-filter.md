# 0062 — Drop citations whose subject is absent from the answer

**Status:** Accepted
**Date:** 2026-11-15

## Context

[ADR-0022](0022-citation-extraction.md) extracts citations from the agent's
**tool-call trace** — every retrieved hit is a citation candidate. That is the
correct structural input for provenance: the panel shows what the agent
actually grounded on, not what a regex found in prose.

[ADR-0023](0023-citation-required-guardrail.md) then gates on `citations.Count
≥ 1`. It does **not** require that a citation's subject appear in the answer.

Live observation (#783): a follow-up "show me other similarly themed games"
(context: Medieval Madness) recommended Hobbit / Halloween / TCM / Evil Dead /
Barnyard, while the Sources panel listed eight OPDB Metadata records —
including **The Jetsons**, which the answer never mentioned. The panel became
an implicit false claim that Jetsons grounded the recommendation.

## Decision

After tool-trace extraction (and multi-turn inheritance), **filter out any
citation whose extractable machine subject does not appear in the answer
prose** before confidence / NoCitation evaluation and before the answer is
returned to the UI.

- Subject is parsed from `Citation.Title` (text before an em-dash / page-range
  suffix; market-value titles strip the `Market value ·` prefix).
- Match is case-insensitive substring; leading articles (`The` / `A` / `An`)
  may be dropped so "The Jetsons" matches an answer that says "Jetsons".
- **Inherited** citations are never dropped — clarifying follow-ups often omit
  the machine name while still relying on prior-turn grounding (#780 contract).
- Titles with **no extractable subject** (e.g. `OPDB record {id}`, Match Play
  standings) fail open and stay — filtering requires a name we can match.

Empty-after-filter still hits ADR-0023's NoCitation gate.

## Consequences

### Positive

- Sources panel subjects are answer-relevant; provenance stays honest.
- Confidence citation-coverage cannot be inflated by unused retrieval hits.
- Pure + deterministic (`AnswerCitedSubjectFilter`) — fully unit-testable;
  no Foundry dependency.

### Negative / deferred

- **Named-without-citation** (the converse of #783: every machine named in a
  recommendation should carry its own citation) is **not** enforced here. That
  needs either richer retrieval discipline or a separate coverage gate; out of
  scope for this ADR.
- Aggressive subject parsing could drop a citation whose title form we do not
  recognize; fail-open for unparseable titles mitigates that.
- Mid-stream `CitationArrived` chunks may briefly surface a hit that the final
  filter later drops — Final remains authoritative (ADR-0026).

## Alternatives considered

1. **Prompt the model not to retrieve unused machines.** Soft; regressions are
   invisible. Rejected — showcase invariant must be structural.
2. **Filter in the Blazor Sources panel only.** Leaves confidence / NoCitation
   seeing unused hits; API consumers still get misleading citations. Rejected.
3. **Require inline `[[cite:N]]` markers for every shown citation.** Re-litigates
   ADR-0022's rejection of citation-emitting tools as the primary path.
   Rejected for v1; markers remain additive.

## Related

- #783 — Sources panel shows machines that appear nowhere in the answer
- [ADR-0022](0022-citation-extraction.md), [ADR-0023](0023-citation-required-guardrail.md)
- [ADR-0026](0026-user-delight-frontend-and-streaming.md) — Final citations authoritative
