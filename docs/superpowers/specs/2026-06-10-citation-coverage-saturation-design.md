# CitationCoverage saturating expectation (ADR-0017 refinement)

**Date:** 2026-06-10 · **Work item:** AB#259 · **Status:** Approved (Jim, 2026-06-10)

## Problem

`ConfidenceCalculator.ComputeCitationCoverage` approximates ADR-0017's `citation_coverage` ("fraction of factual claims with at least one citation") as `min(1, citations / paragraphs)`. Citations are extracted from tool traces — they are answer-level artifacts with no paragraph attribution — so the formula demands per-paragraph resolution that structurally does not exist. Well-grounded single-source answers formatted as 5–6 paragraphs score 0.17–0.20, dragging the geometric-mean composite below the 0.65 threshold.

Evidence (eval `wizard.20260610T094029Z.json`): ev-valuation-0001 refused with signals `r=1.00 m=0.85 c=0.17` (confidence 0.521); ev-repair-0001 refused with `r=1.00 m=0.85 c=0.20` (confidence 0.554). Retrieval succeeded in both; the coverage heuristic alone caused the refusals. Both count against ADR-0017's own calibration target `over_eager_refusal_rate ≤ 0.20`.

## Decision

Replace the per-paragraph expectation with a saturating one — one citation covers up to four paragraphs:

```csharp
coverage = min(1.0, citations / ceil(paragraphs / 4.0))
```

- `ParagraphsPerExpectedCitation = 4`, a named private const and the single tuning knob.
- Zero-citation and empty-answer paths unchanged (0.0 → epsilon floor in `ConfidenceSignals.Composite` → refusal). The "plausible answer with zero citations must not pass" invariant is untouched.
- Calibration: 6-para/1-cite → 0.50 → confidence ≈ 0.75 (answers); 12-para/1-cite → 0.33 → ≈ 0.65 boundary (refuses). Safety gradient for sprawling thin-cited answers is preserved.

Alternatives rejected: binary presence (collapses the safety gradient — a long thinly-cited answer scores 0.95); entity-level coverage (most faithful to the ADR definition but needs machine-name detection in prose — more code, brittle, larger eval-risk surface; revisit if claim-level extraction lands in Phase 6+).

## Docs

Append a dated follow-up entry to `docs/adr/0017-confidence-threshold-refusal.md` (the ADR's own anticipated mechanism for calibration movement): old formula, false-refusal evidence, new formula, before/after eval numbers.

## Tests

Update any `ConfidenceCalculator` tests pinning the old formula. New behavior tests: 6-para/1-cite → 0.5; 12-para/1-cite → ~0.33; ≤4-para/1-cite → 1.0; 0-cite → 0.0; multi-cite saturation at 1.0; empty answer text → 0.0.

## Verification gate

Full `--eval` run before push. Acceptance:

- ev-valuation-0001 and ev-repair-0001 no longer refuse
- `refusal_correctness` ≥ 0.867 (no regression)
- `citation_precision ≥ 0.7` (ADR-0017 target holds)
- No `acceptable_refusal=true` question flips from refusing to answering incorrectly

If any safety-side metric regresses, stop and rethink — do not tune-and-push.
