# Decision log

Sub-ADR decisions for PinballWizard. Append-only. ADRs (in [`adr/`](adr/)) capture architectural decisions with significant trade-offs and alternatives. This log captures the smaller decisions: tool versions within a category, library choices, parameter values, naming conventions, threshold settings — anything worth retrieving later but too small to justify a full ADR.

Per [`guardrails.md`](guardrails.md) § "Decision log": format per entry is

```text
## YYYY-MM-DD — [Short title]
**Decision:** ...
**Alternatives considered:** ...
**Rationale:** ...
**Revisit when:** ...
**Related:** PR #XX, ADR-YYYY (if any)
```

Decisions reverse via a new entry that supersedes the prior one (with a back-reference); never edit history.

## When to add an entry vs. write an ADR

If **all four** of these are true, write an ADR (`adr/00NN-...md`) instead of a decision-log entry:

1. The decision has significant trade-offs.
2. Alternatives were genuinely considered (not default-accepted).
3. Consequences extend beyond the immediate PR.
4. Future readers (including future-Claude) would benefit from the permanent, formally-structured record.

Otherwise, this log is the right home.

---

<!-- New entries append below this marker, newest at the top. -->
