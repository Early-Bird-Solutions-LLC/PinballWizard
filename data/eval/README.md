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

## Phase 4 W1-3 — recuration against deployed Cosmos (2026-05-08)

The v1 ground-truth shipped in Phase 3 PR 8 was hand-curated by a
subagent against plausible OPDB-format ids ("GRBN-MQR4P", etc.). The
deployed Cosmos catalog contains the **actual** OPDB ids and they did
not match — the Phase 3 H2 baseline (`citation_precision=0.133`) was
artificially floored because every successful tool call cited a real
id while `expected_citation_set` held a fictional one. See Phase 3
retrospective lesson 5 in
[`docs/build-spec.md`](../../docs/build-spec.md) and
[`docs/build-spec.md`](../../docs/build-spec.md) § Phase 4 § Scope
item 9 for the spec.

Recuration is performed by [`tools/eval/Recurate.csx`](../../tools/eval/Recurate.csx),
a `dotnet-script` tool that queries the deployed Cosmos `machines`
container for each question's curated machine title and rewrites
`expected_citation_set` with the actual document id. The script reads
its question→title map from
[`tools/eval/wizard.v1.titles.json`](../../tools/eval/wizard.v1.titles.json)
and writes a provenance side-car at
[`data/eval/wizard.v1.recuration.json`](wizard.v1.recuration.json)
recording the recuration timestamp, Cosmos endpoint, jsonl SHA before
recuration, script SHA, and per-question outcome.

**First recuration run (2026-05-08):** 8 of 30 questions resolved
to an actual deployed-Cosmos id (Godzilla, The Wizard of Oz, Dialed
In!). 18 questions did not resolve — their reference machines (Foo
Fighters, Stranger Things, Iron Maiden, The Beatles, AC/DC,
Metallica, Rush) are absent from the current OPDB sync's view of the
catalog or appear only as edition-suffixed records ("AC/DC (Pro)").
Their `expected_citation_set` was left untouched per the script's
no-fabrication contract. 4 out-of-scope rows were skipped (correct).

The 18 unresolved questions are an honest signal, not a bug: until
the deployed catalog actually contains a record the agent could cite,
those questions cannot drive a non-zero `citation_precision` and
keeping the fictional ids in `expected_citation_set` makes that
visible in the metric. Phase 4 follow-up — once the catalog is
re-synced or the questions are re-targeted at machines that ARE in
the catalog, re-run the script. It is idempotent: a re-run that
finds no new matches produces zero diffs.

**Operational note for future recurations:** ensure `az login` is
active on the personal Earlybird subscription before invoking the
script; the script authenticates via `DefaultAzureCredential` to
mirror the production wiring. Always run with `--dry-run` first to
review proposed changes; the dry run prints the same per-question
table as the live run but writes nothing.

### Hardening (2026-05-08) — manufacturer-aware skip-on-mismatch

A spot-check of the W1-3 first run revealed a silent failure mode:
the 3 Godzilla questions in the eval set are intended for **Stern's
2021 Godzilla**, but the deployed Cosmos catalog only contains
**Sega's 1998 Godzilla**. The first version of the recuration script
issued `SELECT TOP 1 c.id ... STRINGEQUALS(c.title, 'Godzilla', true)`
and took the first hit blindly — recording the Sega record's id under
each Godzilla question's `expected_citation_set`. The agent's correct
answer (about Stern 2021) would have failed eval because its citation
wouldn't match the (incorrect) Sega ground truth. Same risk class
exists for any title shared across manufacturers/eras (e.g. multiple
Star Trek records, multiple Avengers).

The hardening (this PR):

- [`tools/eval/wizard.v1.titles.json`](../../tools/eval/wizard.v1.titles.json)
  gains an `expected_manufacturer` column (lowercase string matching
  the deployed catalog's `manufacturer` partition value — e.g.
  `stern`, `jjp`, `sega`, `americanpinball`). All 30 rows are
  curated; out-of-scope rows have `expected_manufacturer=null` to
  match their `machine_title=null`.
- [`tools/eval/Recurate.csx`](../../tools/eval/Recurate.csx) now
  queries Cosmos for **all** title matches (not `TOP 1`), then walks
  the result set and picks the first hit whose `manufacturer`
  matches `expected_manufacturer` (case-insensitive). If no hit
  matches, the row is skipped with a new `mfg_mismatch` outcome and
  the JSONL is left untouched. If `expected_manufacturer` is null on
  an in-scope row, the script falls back to first-hit-wins and logs
  a "manufacturer-unconstrained" warning so a future audit can
  tighten the side-car.
- The recuration manifest's `counts` block gains
  `skipped_mfg_mismatch` and `manufacturer_unconstrained` fields;
  per-row outcomes carry `expected_manufacturer` alongside
  `resolved_manufacturer` for full audit trail.

**Dry-run verification (2026-05-08):** running
`dotnet script tools/eval/Recurate.csx -- --dry-run` against the
same deployed Cosmos endpoint as the W1-3 first run produces:

```text
Questions processed:           30
Recurated (id changed):        0
Unchanged (id matched):        5    (3× The Wizard of Oz, 2× Dialed In!)
Skipped (out-of-scope):        4    (correct refusals)
Skipped (no Cosmos hit):       18   (Foo Fighters / Stranger Things /
                                     Iron Maiden / The Beatles /
                                     AC/DC / Metallica / Rush —
                                     Stern catalog absent from
                                     current OPDB sync)
Skipped (mfg mismatch):        3    (Godzilla ×3 — expected stern,
                                     catalog has sega)
Manufacturer-unconstrained:    0
```

The 3 Godzilla rows are now correctly flagged rather than silently
taking Sega's id. The 5 JJP rows resolve identically to the W1-3
first run (JJP is the only manufacturer that holds those titles).

**Important — this PR ships hardening only.** The live (non-dry-run)
script was deliberately NOT re-run, because the OPDB sync
investigation that's looking into why Stern's modern catalog is
missing from the deployed Cosmos is still open. Running the live
script before that investigation closes would compound the
catalog-state issue. The next live recuration run is sequenced after
the OPDB sync investigation closes; until then, the W1-3 first run's
artifacts (`data/eval/wizard.v1.jsonl` and
`data/eval/wizard.v1.recuration.json`) remain authoritative.
