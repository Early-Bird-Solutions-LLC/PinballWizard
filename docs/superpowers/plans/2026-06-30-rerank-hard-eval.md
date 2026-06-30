# Reranker-Sensitive Hard Eval Golden Set — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a reranker-sensitive hard eval golden set (`data/eval/wizard.hard.v1.jsonl`) plus a retrieval-rank probe and slice-aware scoring, so the Cohere reranker's value can finally be measured and regression-tracked.

**Architecture:** Generate ~50 candidate questions from three sources → a CLI probe runs each through first-stage AI Search retrieval (reranker OFF) and records where the gold chunk ranks → that rank classifies each question into `easy` (1–5) / `reranker-sensitive` (6–10) / `retrieval-miss` (>10) → the eval harness reports metrics per slice. The reranker-sensitive slice's `citation_recall` (reranker-off vs on) is the enablement evidence.

**Tech Stack:** .NET 10 / C# / xUnit / System.CommandLine (CLI) / Azure AI Search (via `IRagRetriever`) / existing `EvaluationHarness` (ADR-0016).

**Spec:** `docs/superpowers/specs/2026-06-30-rerank-hard-eval-design.md`

## Global Constraints

- Target .NET 10; `<Nullable>enable</Nullable>`; zero-warning build (`dotnet build PinballWizard.slnx -warnaserror`).
- Clean Architecture: abstractions in `Application`, implementations in `Infrastructure`; no Infrastructure types leak into Application/Core.
- Commits author as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>` — no Claude attribution trailer.
- Tests assert behavior, not structure (CLAUDE.md): a probe test named "ranks gold chunk at 7" must use a fixture where the gold chunk is actually 7th.
- No XML doc comments on public surface (`feedback_no_xml_docs`).
- Branch: `feat/rerank-hard-eval` (already created).
- Pre-push gate: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.
- Live runs (probe, A/B) use the isolated session: `AZURE_CONFIG_DIR=~/.azure-pinwiz`, `AZURE_TOKEN_CREDENTIALS=dev`, `ASPNETCORE_ENVIRONMENT=Development`, plus the Cosmos / AiFoundry / AiSearch endpoints from `docs/operations.md`.

---

## File Map

**Modify:**
- `src/PinballWizard.Application/Ai/Evaluation/EvalQuestion.cs` — add `Slice`, `Source`, `FirstStageRank` optional fields (Task 1).
- `src/PinballWizard.Application/Ai/Evaluation/EvalResult.cs` — add per-slice aggregate breakdown (Task 4).
- the `EvaluationHarness` implementation under `src/PinballWizard.Infrastructure/Integrations/Foundry/` — group aggregates by slice (Task 4).
- `src/PinballWizard.Cli/Program.cs` — register `--probe-retrieval <path>` verb (Task 3).

**Create:**
- `src/PinballWizard.Application/Ai/Retrieval/IRetrievalRankProbe.cs` + `RetrievalRankResult.cs` (Task 2).
- `src/PinballWizard.Infrastructure/Rag/Retrieval/RetrievalRankProbe.cs` (Task 2).
- `data/eval/wizard.hard.candidates.v1.jsonl` (Task 5 — candidates) → `data/eval/wizard.hard.v1.jsonl` (Task 6 — classified).
- tests under `tests/PinballWizard.Application.Tests/` and `tests/PinballWizard.Infrastructure.Tests/`.

---

## Task 1: EvalQuestion slice/source/rank fields

**Files:**
- Modify: `src/PinballWizard.Application/Ai/Evaluation/EvalQuestion.cs`
- Test: `tests/PinballWizard.Application.Tests/Ai/Evaluation/EvalQuestionParserTests.cs` (existing — add a case)

**Interfaces:**
- Produces: three new optional `EvalQuestion` properties — `Slice` (`string?`, json `slice`), `Source` (`string?`, json `source`), `FirstStageRank` (`int?`, json `first_stage_rank`). All optional so `wizard.v2.jsonl` rows parse unchanged.

- [ ] **Step 1: Write the failing parser round-trip test**

In `EvalQuestionParserTests.cs`, add:

```csharp
[Fact]
public void Parse_HardEvalSliceFields_RoundTrip()
{
    var line = """
    {"id":"hard-0001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["mch_X"],"acceptable_refusal":false,"slice":"reranker-sensitive","source":"confusable-edition","first_stage_rank":7}
    """;
    var q = EvalQuestionParser.ParseLine(line);
    Assert.Equal("reranker-sensitive", q.Slice);
    Assert.Equal("confusable-edition", q.Source);
    Assert.Equal(7, q.FirstStageRank);
}
```

(If the parser's per-line entry point is named differently than `ParseLine`, match the existing test file's call — read it first.)

- [ ] **Step 2: Run it, verify FAIL** — `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "Parse_HardEvalSliceFields_RoundTrip"` → FAIL (properties don't exist).

- [ ] **Step 3: Add the fields** to the `EvalQuestion` record (append after `AcceptableSubAgents`, keep them optional):

```csharp
    [property: JsonPropertyName("slice")] string? Slice = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("first_stage_rank")] int? FirstStageRank = null);
```

- [ ] **Step 4: Run the test, verify PASS.**

- [ ] **Step 5: Run the existing parser suite** — `dotnet test ... --filter "EvalQuestionParser"` → all PASS (v2 rows still parse; new fields default null).

- [ ] **Step 6: Commit** — `git commit -m "feat(eval) EvalQuestion slice/source/first_stage_rank fields for the hard set"`

---

## Task 2: Retrieval-rank probe (core)

**Files:**
- Create: `src/PinballWizard.Application/Ai/Retrieval/IRetrievalRankProbe.cs`, `.../RetrievalRankResult.cs`
- Create: `src/PinballWizard.Infrastructure/Rag/Retrieval/RetrievalRankProbe.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Rag/Retrieval/RetrievalRankProbeTests.cs`

**Interfaces:**
- Produces:
  - `RetrievalRankResult(int? GoldRank, string Slice, IReadOnlyList<string> TopChunkCitations)` — `GoldRank` null = not retrieved; `Slice` ∈ {`easy`,`reranker-sensitive`,`retrieval-miss`} derived from `GoldRank` and the top-N cutoff.
  - `IRetrievalRankProbe.ProbeAsync(EvalQuestion question, int topN, CancellationToken ct) : Task<RetrievalRankResult>`.
- Consumes: `IRagRetriever.RetrieveAsync` (Task is read-only on retrieval). The probe MUST run first-stage order — construct/resolve a retriever path with the cross-encoder OFF (so the returned order is the AI Search hybrid+semantic order, not reranked).
- Citation matching: reuse the same gold-citation↔chunk matching the citation evaluators use. Read `src/PinballWizard.Application/Ai/Evaluation/Evaluators/CitationPrecisionEvaluator.cs` and extract the chunk→citation-id projection into a shared helper (or call it) so the probe and the evaluator agree on what "the gold chunk" is. A retrieved `RetrievedChunk` matches a gold citation when its projected citation id is in the question's `ExpectedCitationSet` (or any set in `AcceptableCitationSets`).

- [ ] **Step 1: Write the failing test** with a fake `IRagRetriever` returning 10 chunks where the gold chunk (matching `expected_citation_set`) is 7th:

```csharp
[Fact]
public async Task ProbeAsync_GoldChunkSeventh_ClassifiesRerankerSensitive()
{
    var chunks = Enumerable.Range(1, 10)
        .Select(i => MakeChunk(machineId: i == 7 ? "GOLD" : $"other{i}", score: 1.0 - i * 0.05))
        .ToList();
    var probe = new RetrievalRankProbe(new FakeRetriever(chunks), /* citation projector */ );
    var q = MakeQuestion(expectedCitationSet: new[] { "mch_GOLD" });

    var result = await probe.ProbeAsync(q, topN: 5, CancellationToken.None);

    Assert.Equal(7, result.GoldRank);
    Assert.Equal("reranker-sensitive", result.Slice);  // 6..10
}
```

Plus two more: gold at rank 3 → `easy`; gold absent → `GoldRank == null`, `retrieval-miss`. (Use the real `mch_`→chunk projection so the test exercises actual matching, not a stub.)

- [ ] **Step 2: Run, verify FAIL** (types don't exist).

- [ ] **Step 3: Implement** `IRetrievalRankProbe` + `RetrievalRankResult` (Application) and `RetrievalRankProbe` (Infrastructure). Algorithm: call retrieval (reranker off, TopK from `RetrievalOptions`), enumerate the returned chunks in order, project each to its citation id, find the 1-based index of the first chunk matching the gold set; `GoldRank = that index or null`. Slice: `null → retrieval-miss`; `<= topN → easy`; else `reranker-sensitive`.

- [ ] **Step 4: Run tests, verify PASS** (all three classification cases).

- [ ] **Step 5: Commit** — `git commit -m "feat(eval) retrieval-rank probe: classify gold-chunk first-stage rank"`

---

## Task 3: `--probe-retrieval` CLI verb

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs` (register the option + handler, mirroring the existing `--eval` block ~line 760).
- Test: `tests/PinballWizard.Cli.Tests/` — a smoke test that the verb parses and exits 2 when AI Search isn't configured (mirror the `--eval` exit-code-2 pattern).

**Interfaces:**
- Consumes: `IRetrievalRankProbe` (Task 2), `EvalQuestionParser`.
- Behavior: `--probe-retrieval <input.jsonl>` reads each `EvalQuestion`, calls `ProbeAsync`, and writes `<input>.classified.jsonl` where each row is the original question plus `slice` + `first_stage_rank` populated. Prints a slice-distribution summary line (`easy=N reranker-sensitive=N retrieval-miss=N`) so a run is greppable.

- [ ] **Step 1: Write the failing CLI smoke test** (verb registered; with no `AiSearch:Endpoint`, exits 2 with a remediation message — copy the assertion shape from the existing `--eval` CLI test).
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** the `--probe-retrieval` option + action: resolve `IRetrievalRankProbe` (null-check → exit 2 like `--eval`), parse the input jsonl, probe each row, write the `.classified.jsonl`, print the summary.
- [ ] **Step 4: Run the test, verify PASS.**
- [ ] **Step 5: Update `--help`/docs string** for the new verb (note it requires AI Search + reranker-off).
- [ ] **Step 6: Commit** — `git commit -m "feat(cli) --probe-retrieval verb: classify a candidate eval jsonl by retrieval rank"`

---

## Task 4: Slice-aware harness scoring

**Files:**
- Modify: `src/PinballWizard.Application/Ai/Evaluation/EvalResult.cs` — add `IReadOnlyDictionary<string, EvalAggregate> BySlice` (or a list of `(slice, aggregate)`), populated when rows carry `Slice`.
- Modify: the `EvaluationHarness` implementation — after scoring all rows, group by `EvalQuestion.Slice` and compute the same aggregate per group; null slice rows go to a `"(unsliced)"` bucket so v2 behavior is unchanged.
- Test: `tests/PinballWizard.Infrastructure.Tests/.../EvaluationHarnessTests.cs` (or wherever the harness aggregate is unit-tested) — feed rows with two slices, assert per-slice means.

**Interfaces:**
- Produces: per-slice aggregate in the result JSON; the existing top-level aggregate is unchanged (still the overall mean).

- [ ] **Step 1: Read the harness aggregation** (find where `EvalResult.Aggregate` is computed) and the existing aggregate test, to match types/structure.
- [ ] **Step 2: Write the failing test** — two rows tagged `slice:"easy"`, two `slice:"reranker-sensitive"`, assert `result.BySlice["reranker-sensitive"].CitationRecallMean` equals the mean of just those two.
- [ ] **Step 3: Run, verify FAIL.**
- [ ] **Step 4: Implement** the per-slice grouping (reuse the existing aggregate computation over each slice's rows — DRY: extract the aggregate function if it's inline).
- [ ] **Step 5: Run tests, verify PASS; run full eval-harness suite — no regression** (v2 result shape: top-level aggregate identical; `BySlice` has one `(unsliced)` bucket).
- [ ] **Step 6: Commit** — `git commit -m "feat(eval) per-slice aggregate breakdown in the harness result"`

---

## Task 5: Candidate generation + ground-truth curation (content)

**Deliverable:** `data/eval/wizard.hard.candidates.v1.jsonl` — ~50 rows, each a valid `EvalQuestion` with `source` set, and an accurate `expected_citation_set` / `acceptable_citation_sets` curated against the live corpus.

This is research/curation, not TDD. Process:

- [ ] **Step 1: Enumerate the indexed corpus** — query AI Search `pinwiz-rag-v1` (or read `data/phase4/curated-subset.*.json`) for the machines/documents actually indexed, so every gold citation is real.
- [ ] **Step 2: Draft ~20 confusable multi-edition questions** — for machines with multiple editions/near-duplicate content (AFM Remake vs original, Godzilla/Foo Fighters editions). Each targets a passage that differs by edition; gold = the specific edition's chunk. Reuse `acceptable_citation_sets` for genuinely-either cases (per `project_eval_noise_and_afm_remake_drift`).
- [ ] **Step 3: Draft ~18 adversarial/indirect questions** — paraphrased / multi-hop / lexically-distant phrasings whose answer lives in a specific chunk.
- [ ] **Step 4: Draft ~12 corpus-mined direct questions** — pulled from real manual/rulesheet/bulletin passages, each citing one identifiable chunk.
- [ ] **Step 5: Validate each gold citation exists** in the corpus (the probe in Task 6 confirms retrievability; a citation that never retrieves = fix or drop the row).
- [ ] **Step 6: Commit** — `git commit -m "data(eval) hard-set candidate questions (3 sources, pre-classification)"`

---

## Task 6: Classify candidates → `wizard.hard.v1.jsonl`

- [ ] **Step 1: Run the probe live** (isolated session) — `dotnet run --project src/PinballWizard.Cli -- --probe-retrieval data/eval/wizard.hard.candidates.v1.jsonl`. Produces `…candidates.v1.classified.jsonl` with `slice` + `first_stage_rank` per row + the distribution summary.
- [ ] **Step 2: Triage** — `retrieval-miss` rows: confirm the gold genuinely isn't retrievable (fix the question, or keep as a logged first-stage-gap example). Move the curated, classified rows into `data/eval/wizard.hard.v1.jsonl`.
- [ ] **Step 3: Record the slice distribution** in the spec/ADR follow-up (esp. the `reranker-sensitive` count — if small, say so honestly).
- [ ] **Step 4: Commit** — `git commit -m "data(eval) wizard.hard.v1.jsonl — classified hard golden set"`

---

## Task 7: Reranker A/B on the hard slice → enablement evidence

- [ ] **Step 1: Run the harness on the hard set, reranker OFF** (isolated session, `Rag__CrossEncoder__Enabled=false`, `Evaluation__GroundTruthPath=data/eval/wizard.hard.v1.jsonl` — section is `Evaluation`, NOT `EvalHarness`; the wrong key is silently ignored and the harness runs the default 42-question v2 set). Because the hard set is small, it completes within `RunTimeoutSeconds`.
- [ ] **Step 2: Run reranker ON** (`Rag__CrossEncoder__Enabled=true`). Repeat each arm ≥2× (eval is noisy — `project_eval_noise_and_afm_remake_drift`).
- [ ] **Step 3: Compare the `reranker-sensitive` slice** `citation_recall` (primary) + `coverage` (secondary), off vs on. A clear positive delta = the reranker's measured value; a null delta = the reranker doesn't help even where it structurally could — both are decision-grade.
- [ ] **Step 4: Write up the result** in ADR-0024 (append an "H5b-hard outcome" section) and `build-spec.md` §Phase 4.5; this is the evidence for the `Rag:CrossEncoder:Enabled` production decision (which, if enabled, also needs the fixed reranker image deployed — already on main via #586).
- [ ] **Step 5: Commit** — `git commit -m "docs(eval) hard-slice reranker A/B outcome + enablement recommendation"`

---

## Self-Review

**Spec coverage:** probe (T2/T3) ✓, 3-source generation (T5) ✓, classify-into-slices (T2/T6) ✓, `wizard.hard.v1.jsonl` schema + slice tags (T1/T6) ✓, slice-aware scoring (T4) ✓, recall-primary metric (T7) ✓, alongside-not-replacing v2 (T6) ✓, ~50 size + emergent reranker-sensitive slice (T5/T6) ✓.

**Placeholder scan:** Tasks 2–4 reference files an implementer must read before writing final code (the citation projector in `CitationPrecisionEvaluator`, the harness aggregate function, the existing `--eval`/parser call shapes) — these are deliberate "read this exact file first" pointers, not vague TODOs, because the precise signatures live in code not yet read in this plan. Each names the exact file + what to extract.

**Type consistency:** `Slice`/`Source`/`FirstStageRank` (T1) are the same names consumed in T2/T4/T6. `RetrievalRankResult.GoldRank`/`Slice` (T2) feed the CLI writer (T3). Slice vocabulary (`easy`/`reranker-sensitive`/`retrieval-miss`) is consistent across T2/T4/T6/T7.

**Note on TDD scope:** Tasks 1–4 are code (full TDD). Tasks 5–7 are content/operational (curation + live measurement) with concrete deliverables and commit points, but no unit-test cycle — they can't be TDD'd and shouldn't be faked as such.
