# Eval set — Phase 3 ground truth (`wizard.v1.jsonl`)

This directory holds the held-out evaluation set used by the Phase 3
evaluation harness ([ADR-0016](../../docs/adr/0016-evaluation-harness.md))
plus the timestamped JSON results files.

## Format

`wizard.v1.jsonl` is JSON-Lines. One question per line. Blank lines and
lines whose first non-whitespace character is `#` are ignored — the
parser uses the latter as a curator-only convention for section headings.

Per-question shape:

```json
{
  "id": "ev-rules-0001",
  "question": "What's the wizard mode in Stern Foo Fighters?",
  "expected_sub_agent": "Rules",
  "expected_citation_set": ["GRBN-MQR4P"],
  "acceptable_refusal": false,
  "notes": "Optional curator note."
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `id` | yes | Unique within the file. Convention: `ev-{subagent}-{nnnn}`. |
| `question` | yes | The user prompt verbatim. |
| `expected_sub_agent` | yes | One of `Wizard`, `Valuation`, `Rules`, `Repair`. Out-of-scope rows route to `Wizard`. |
| `expected_citation_set` | yes | Array of raw OPDB ids (no `mch_` prefix) the answer should cite. Empty list when `acceptable_refusal=true`. |
| `acceptable_refusal` | yes | `true` when "I don't know" is the correct response (out-of-scope, missing grounding). |
| `notes` | no | Curator-only context. |

The harness extracts predicted citation ids from the agent's
`WizardAnswer.Citations[].MachineId` — the same OPDB id the AiRouter
extracts from the `https://opdb.org/machines/<id>` URL pattern in the
agent's text. Hence the raw-OPDB-id format here.

## Curation conventions

- **Bias toward simple lookups.** v1 is hand-curated against real OPDB
  machine records (Stern Foo Fighters, Godzilla, Iron Maiden, etc.) so
  every grounded question has a stable, citable id. The bias is
  acknowledged: this is a regression-detection floor, not a coverage
  surface. Phase 4 grows the set when RAG ground-truth (manuals,
  bulletins) becomes citable. See ADR-0016 § Negative-consequences and
  build-spec § Phase 3 risk P3-R8.
- **Out-of-scope rows are deliberate.** A handful of questions
  (`ev-valuation-0010`, `ev-repair-0009`, `ev-repair-0010`) are
  out-of-scope on purpose so refusal-correctness has signal in both
  directions: the agent is rewarded for refusing them, and an
  over-eager fabricated answer scores 0 on `refusal_correctness`.
- **Citation precision is the load-bearing metric.** Per
  [`guardrails.md`](../../docs/guardrails.md) goal #5 (provenance is
  sacred), an answer that cites a wrong machine id is a worse failure
  than an answer that cites nothing. Precision penalizes the former
  hard; recall penalizes the latter. Both numbers travel in every
  results file.

## Running the harness

From the repo root, with `AiFoundry:ProjectEndpoint` configured:

```text
dotnet run --project src/PinballWizard.Cli -- --eval
```

The CLI exits 2 with a remediation hint when AI Foundry isn't
configured. On success, it prints the results path + the four
aggregate scores. The full per-question detail lives in the JSON file.

## Results JSON

Each run produces `data/eval/results/wizard.{yyyyMMddTHHmmssZ}.json`
containing the run metadata + per-question scores + an aggregate
block. Shape: `EvalRunResult` in
`src/PinballWizard.Application/Ai/Evaluation/EvalResult.cs`. The
`aggregate` object holds the four headline metrics:

```json
"aggregate": {
  "question_count": 30,
  "error_count": 0,
  "citation_precision_mean": 0.86,
  "citation_recall_mean": 0.74,
  "subagent_accuracy_mean": 0.93,
  "refusal_correctness_mean": 1.00
}
```

A 5% citation-precision regression vs the rolling 30-day baseline is
a deploy-blocking event per
[`guardrails.md`](../../docs/guardrails.md) § Run-time triggers.
Phase 3 ships the metric; Phase 6 wires the actual
gate-enforcement automation.

## Phase 3 implementation note

Per [ADR-0016](../../docs/adr/0016-evaluation-harness.md), the four
custom evaluators (citation precision/recall, subagent accuracy,
refusal correctness) are **code-based evaluators**. The .NET classes in
`src/PinballWizard.Application/Ai/Evaluation/Evaluators/` are the
canonical Phase 3 runtime; equivalent Python snippets live in
`src/PinballWizard.Infrastructure/Integrations/Foundry/EvaluatorPythonSpecs.cs`
as the spec for future Foundry-side registration. The
`Azure.AI.Projects` 2.0.1 GA SDK does not yet expose a public
`ProjectEvaluators.CreateVersionAsync` accessor (it's gated behind
the `AAIP001` experimental diagnostic and the operations-client method
is non-public); a future SDK version will flip the harness's planned-
registration noop into a real round-trip without changing
`IEvaluationHarness` or the results JSON shape.
