# 0016 — Evaluation harness: custom citation-accuracy evaluator + Foundry built-ins via `EvaluationClient`

**Status:** Accepted
**Date:** 2026-05-04

## Context

Phase 3 introduces the AI orchestration layer
([ADR-0014](0014-microsoft-foundry-orchestration.md)). To detect
regression across prompt changes, model swaps, threshold tweaks,
and Phase 4 RAG plumbing additions, we need a stable, repeatable
evaluation mechanism — not "did the demo question still work?"
but a proper held-out eval set with quantitative metrics.

[`quality-spec.md`](../quality-spec.md) § Test quality places this
gate in Phase 3 and names citation-accuracy as the v1 launch target
(≥95%). [`guardrails.md`](../guardrails.md) § Run-time triggers
defines a 5% citation-accuracy regression as a deploy-blocking
event. Both rest on an eval harness that produces a reproducible
number across runs.

`Azure.AI.Projects` 2.0 (GA, April 2026) ships a fully programmatic
evaluation surface via `EvaluationClient`:

- **Built-in evaluators**: `builtin.violence`, `builtin.fluency`,
  `builtin.task_adherence`, plus others
- **Custom prompt-based evaluators**: register a
  `PromptBasedEvaluatorDefinition` with prompt text + scoring scale
- **Custom code-based evaluators**: register custom code that scores
  responses with arbitrary logic
- **Run results are JSON, retrievable in code** via
  `GetEvaluationRunOutputItemsAsync` — fully git-committable
- **`EvaluationRule`** for continuous evaluation (auto-eval on
  every response)
- **`ProjectsSchedule`** for daily evaluation runs

This invalidates an earlier draft that framed Foundry-portal eval
as "not git-committable." With 2.0, the entire evaluation surface
is programmatic and JSON-output-able, while still benefiting from
Foundry's evaluator runtime, scheduling, and continuous-eval
infrastructure.

## Decision

We build the citation-accuracy harness as a **custom code-based
evaluator registered with Foundry's `EvaluationClient`**, paired
with selected Foundry built-in evaluators for general quality
signals. The harness runs from the CLI, commits JSON results, and
gates on the citation-accuracy metric.

### CLI

```text
dotnet run --project src/PinballWizard.Cli -- --eval [--write-baseline]
```

Reads `data/eval/wizard.v1.jsonl`, runs each question through
`IAiRouter` (which exercises the full agent-framework dispatch
path), invokes Foundry's `EvaluationClient` with a registered
testing-criteria set, writes
`data/eval/results/wizard.{timestamp}.json`.

### Ground-truth shape (`data/eval/wizard.v1.jsonl`)

One JSON object per line:

```json
{
  "id": "ev-rules-0001",
  "question": "What's the wizard mode in Stern Foo Fighters?",
  "expected_sub_agent": "Rules",
  "expected_citation_set": ["mch_<opdb-id>"],
  "acceptable_refusal": false,
  "notes": "Hand-curated from OPDB record; rules text in description"
}
```

Initial size: ~30 questions, ~10 per sub-agent, hand-curated by
sampling 50 random OPDB machines and writing one factual question
per machine for which the OPDB record itself is the citation. This
biases toward simple lookups (regression-detection floor, not
coverage). Phase 4 grows the set when RAG ground-truth (manuals,
bulletins) becomes citable. The bias is documented in
`data/eval/README.md` per risk P3-R8.

### Testing criteria registered with `EvaluationClient`

The harness registers a multi-evaluator testing set on every run:

| Evaluator | Source | Purpose |
| --- | --- | --- |
| `pinwiz.citation_precision` | **Custom code-based** evaluator (ours) | Fraction of predicted citations that appear in the expected set — *the load-bearing showcase metric* |
| `pinwiz.citation_recall` | **Custom code-based** evaluator (ours) | Fraction of expected citations that appear in the predicted set |
| `pinwiz.subagent_accuracy` | **Custom code-based** evaluator (ours) | Fraction of questions routed to the expected sub-agent (read from Foundry trace via `gen_ai.*` attributes) |
| `pinwiz.refusal_correctness` | **Custom code-based** evaluator (ours) | Refusal precision + recall combined into a single agreement score |
| `builtin.task_adherence` | **Foundry built-in** | Did the agent actually answer the question asked, without drifting? |
| `builtin.fluency` | **Foundry built-in** | Linguistic quality of the answer |

Custom evaluators are registered with the Foundry project once on
startup via `EvaluationClient.RegisterEvaluator(...)`-style calls
(implementation pattern from
[Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.projects-readme?view=azure-dotnet)
§ Evaluations § Using custom code-based evaluator). Built-in
evaluators are selected by name; no registration needed.

### Run shape

```csharp
// Pseudocode — actual implementation in src/PinballWizard.Application/Ai/Evaluation/
var evaluation = await evaluationClient.CreateEvaluationAsync(
    name: "pinwiz-wave3-baseline",
    testingCriteria: [
        new { type = "azure_ai_evaluator", name = "citation_precision",
              evaluator_name = "pinwiz.citation_precision", ... },
        new { type = "azure_ai_evaluator", name = "task_adherence",
              evaluator_name = "builtin.task_adherence", ... },
        // ...
    ],
    dataSourceConfig: customSchemaWithExpectedSetMapping);

var run = await evaluationClient.CreateEvaluationRunAsync(
    evaluationId: evaluation.Id,
    dataSource: new { type = "azure_ai_target_completions",
                      target = new { type = "azure_ai_agent",
                                     name = "Wizard",
                                     version = currentAgentVersion },
                      source = new { type = "file_content",
                                     content = evalSetJsonl } });

// Wait for completion, then retrieve JSON results
var results = await GetResultsListAsync(evaluationClient, evaluation.Id, run.Id);
File.WriteAllText("data/eval/results/wizard.{timestamp}.json", ...);
```

The run uses Foundry's `azure_ai_agent` target type so the
evaluation invokes the deployed agent (not a re-implementation in
test code). Costs flow through the Foundry project the same as
production calls — visible in the daily KQL aggregation.

### Output artifact

`data/eval/results/wizard.{timestamp}.json` is **committed** at H2
(operational hand-off, scope item 13). Future eval runs commit
their own files; the metric trend is visible via `git diff`
between result files. Aggregate metrics also append to
`data/eval/results/trend.csv` for human-readable scanning.

Per-question `gen_ai.*` trace attributes are correlated into the
JSON output (thread ID, span ID, per-call model, per-call token
counts) so a regression can be traced through the full agent path
without re-running.

### Deploy gate

A 5% regression in `pinwiz.citation_precision` or
`pinwiz.citation_recall` relative to the rolling 30-day baseline
is a deploy-blocking event per
[guardrails.md](../guardrails.md) § Run-time triggers. Phase 3
ships the metric; the actual gate-enforcement automation is
Phase 6 (operability).

### Continuous + scheduled eval (Phase 6 forward-compat)

`EvaluationRule` (continuous eval on every response) and
`ProjectsSchedule` (daily eval runs) are noted but **not enabled
in Phase 3**. Phase 6 turns them on once the operability dashboards
are ready to consume the signals. Phase 3's hand-off run is a
single explicit eval invocation; Phase 6 promotes it to scheduled.

## Consequences

**Positive:**

- The citation-accuracy metric is the load-bearing eval signal for
  the showcase (provenance is sacred per
  [guardrails.md](../guardrails.md) goal #5); we own its definition
  and computation, and it runs inside Foundry's evaluator runtime
  alongside platform-built-in evaluators — best of both worlds.
- Evaluation runs through the deployed agent (`azure_ai_agent`
  target), not a re-implementation in test code. The evaluator
  exercises exactly the production path; no contract drift between
  test and prod (the lesson from DL-0002 / DL-0003 carried into
  Phase 3).
- Eval results are committed JSON: a prospect reviewing the repo
  sees the metric trajectory in `git log`, the same way they see
  the test count's trajectory.
- Built-in evaluators (`task_adherence`, `fluency`) catch quality
  regressions our citation-only metric wouldn't see (e.g., the
  agent drifts from the question; the answer becomes unreadable).
  Free signals; just register them in the testing-criteria set.
- Continuous-eval and scheduled-eval primitives are inherited for
  free in Phase 6 — no new infrastructure to build.
- Cost predictable: ~$0.50 per eval run × ~5 runs/month ≈ $2.50/mo,
  well under the $400/mo cap.

**Negative:**

- Eval set is hand-curated and biased toward simple lookups.
  Phase 4 must grow it; if Phase 4 ships before the eval set is
  grown, the pass-rate will trend artificially high. Mitigation:
  documented in `data/eval/README.md` and risk P3-R8.
- Custom code-based evaluators require registration with the
  Foundry project; this is an extra startup step the harness owns.
  If the project is recreated (e.g., DR drill), the evaluators
  must be re-registered. Mitigation: idempotent registration on
  every harness run.
- Evaluator-runtime cost: Foundry built-in evaluators consume
  tokens too (LLM-as-judge for `task_adherence`, etc.). The ~$0.50
  per-eval-run estimate accounts for this.
- The `azure_ai_agent` target requires the deployed agent to exist
  (so this scope item depends on Wave 1 H1 hand-off being done
  before scope item 13 H2 hand-off). Already captured in the
  build-spec § Parallelism plan dependency core.
- Foundry's evaluation API surface evolves with the SDK; while 2.0
  is GA, evaluator-registration patterns may shift across minor
  versions. Mitigation: pin SDK version; integration tests against
  deployed Foundry exercise the registration path.

## Alternatives considered

- **Foundry-portal evaluators only** (no custom code).
  Rejected: citation-accuracy is the showcase metric and must be
  custom; portal-only evaluators don't include it.
- **Pure custom harness, no Foundry evaluators** (the original
  draft). Rejected: misses task-adherence + fluency signals that
  Foundry built-ins provide for free, and skips the
  continuous-eval / scheduled-eval infrastructure that Phase 6
  inherits.
- **LLM-as-judge with no quantitative metric.** Rejected:
  subjective judgments don't trend cleanly across runs, can't gate
  a deploy.
- **Citation-accuracy by exact-string match against ground-truth
  text.** Rejected: too brittle to LLM rephrasing. Citation IDs
  are stable; citation text isn't.
- **No eval harness in Phase 3 (defer to Phase 4 with RAG).**
  Rejected: Phase 3 ships the thin Wizard slice; without a
  baseline before Phase 4 lands, regression detection has no
  anchor.
- **Eval harness as a separate test project.** Rejected: the eval
  is a CLI artifact, not a unit-test suite. It runs intentionally
  outside the test runner because it hits real Foundry; conflating
  with `dotnet test` is the wrong mental model.
- **Custom prompt-based evaluator (instead of code-based) for
  citation-accuracy.** Rejected: citation-accuracy is a deterministic
  set-overlap calculation, not a judgment call. Code-based is
  cheaper (no LLM-as-judge call) and exact (no scoring drift).

## References

- [Azure AI Projects 2.0 Evaluations docs](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.projects-readme?view=azure-dotnet)
  — `EvaluationClient`, custom evaluators, evaluation rules
- [ADR-0014](0014-microsoft-foundry-orchestration.md) — agent
  framework + agents the eval exercises
- [ADR-0015](0015-cost-routing-and-semantic-cache.md) — OTel GenAI
  semconv attributes correlated by the harness
- [ADR-0017](0017-confidence-threshold-refusal.md) — confidence
  threshold the eval helps calibrate at H2; refusal categories
- [ADR-0018](0018-prompt-management.md) — prompt versioning that
  the eval tags into result JSON
- [build-spec.md § Phase 3](../build-spec.md) — scope item 12 (eval
  harness) and item 13 (H2 baseline run + threshold calibration)
- [quality-spec.md](../quality-spec.md) § Test quality — eval gate
  placement
- [guardrails.md](../guardrails.md) § Run-time triggers — 5%
  regression deploy-block
