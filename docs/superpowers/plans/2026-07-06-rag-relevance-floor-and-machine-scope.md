# RAG Relevance Floor + Machine-Scope Retention Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the Wizard from surfacing unrelated machines' records by (A) fixing the reranker-score-scale mismatch so the relevance floor can actually cut low-relevance results, and (B) preserving the `machineId` filter on the corpus-search retry.

**Architecture:** Introduce one shared `RetrievalScoring` helper in the Application layer that normalizes the Azure semantic reranker score (0–4) to a 0–1 fraction. Both the retriever's minimum-score floor (Infrastructure) and the citation "% match" badge (Web) delegate to it — a single source of truth that makes the 0–4-vs-0–1 divergence structurally impossible. Amend the Wizard prompt so a machine-grounded retry never widens to a corpus-wide search, and add eval regression fixtures.

**Tech Stack:** .NET 10, C#, xUnit, bUnit (Web tests), Azure AI Search SDK, Microsoft Agent Framework (prompt is an embedded `.md`).

## Global Constraints

- **Target framework:** .NET 10; `<Nullable>enable</Nullable>` — every file.
- **Commit identity:** `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. **No Claude attribution trailer** (matches repo history — INVARIANT).
- **Tests assert behavior, not structure.** A relevance-floor test must include a fixture where the floor actually drops a result (the 28%-match case).
- **No XML doc comments** on public surface (`feedback_no_xml_docs`). Use `//` comments for rationale.
- **No guessing values.** OPDB ids in eval fixtures MUST be resolved from a real source, never invented (Task 5, Step 4).
- **Full CI-equivalent suite before push:** `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.
- **Working directory:** the worktree `c:\earlybird\PinballWizard\.worktrees\rag-relevance-floor` on branch `feat/rag-relevance-floor-machine-scope`. All paths below are repo-relative to it.
- **The 0.35 floor value is an operational admin-key setting, NOT a code default.** `RetrievalOptions.MinimumScore` stays `0.0` (safe for CLI/fixtures). The live floor is set via the `rag.retrieval_minimum_score` runtime key after deploy (Task 6).

---

## File Structure

**Create:**
- `src/PinballWizard.Application/Ai/Retrieval/RetrievalScoring.cs` — shared reranker-score normalization (the single source of truth).
- `tests/PinballWizard.Application.Tests/Application/Ai/RetrievalScoringTests.cs` — unit tests for the helper.

**Modify:**
- `src/PinballWizard.Web/Components/Citations/CitationCard.razor` — `MatchPercent` delegates to `RetrievalScoring`; remove the local `MaxRerankerScore` constant.
- `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchRagRetriever.cs` — normalize the score before the minimum-score comparison via a new testable `PassesMinimumScore` helper.
- `src/PinballWizard.Application/Ai/Hosting/WellKnownSettings.cs` — correct the wrong "0.0..1.0 raw scale" comment.
- `src/PinballWizard.Application/Ai/Agents/Wizard.md` — preserve `machineId` on the retry.
- `data/eval/wizard.v2.jsonl` — add the two regression fixtures.
- `tests/PinballWizard.Web.Tests/Components/Citations/CitationCardTests.cs` — add the cross-layer parity test.
- `tests/PinballWizard.Infrastructure.Tests/Rag/Retrieval/AiSearchRagRetrieverTests.cs` — add the floor-boundary + >1.0 reranker tests.
- `tests/PinballWizard.Infrastructure.Tests/Ai/Evaluation/EvalGroundTruthFileTests.cs` — assert the new fixture rows.

---

### Task 1: Shared reranker-score normalization helper

**Files:**
- Create: `src/PinballWizard.Application/Ai/Retrieval/RetrievalScoring.cs`
- Test: `tests/PinballWizard.Application.Tests/Application/Ai/RetrievalScoringTests.cs`

**Interfaces:**
- Produces: `PinballWizard.Application.Ai.Retrieval.RetrievalScoring` with `public const double MaxRerankerScore = 4.0;` and `public static double NormalizeRerankerScore(double rawScore)` returning a value in `[0.0, 1.0]`.

- [ ] **Step 1: Write the failing test**

Create `tests/PinballWizard.Application.Tests/Application/Ai/RetrievalScoringTests.cs`:

```csharp
using PinballWizard.Application.Ai.Retrieval;
using Xunit;

namespace PinballWizard.Application.Tests.Application.Ai;

public class RetrievalScoringTests
{
    [Fact]
    public void MaxRerankerScore_IsFour() =>
        Assert.Equal(4.0, RetrievalScoring.MaxRerankerScore);

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.12, 0.28)]   // the "28% match" card from the Cactus Canyon incident
    [InlineData(1.6, 0.40)]
    [InlineData(3.4, 0.85)]
    [InlineData(4.0, 1.0)]
    [InlineData(8.0, 1.0)]     // BM25 fallback above the ceiling clamps to 1.0
    [InlineData(-0.5, 0.0)]    // defensive: never negative
    public void NormalizeRerankerScore_MapsToClampedFraction(double raw, double expected) =>
        Assert.Equal(expected, RetrievalScoring.NormalizeRerankerScore(raw), precision: 6);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "FullyQualifiedName~RetrievalScoring"`
Expected: FAIL to compile — `RetrievalScoring` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/PinballWizard.Application/Ai/Retrieval/RetrievalScoring.cs`:

```csharp
namespace PinballWizard.Application.Ai.Retrieval;

// Single source of truth for converting a raw Azure AI Search relevance
// score into a normalized 0–1 fraction. Both the retriever's minimum-score
// floor (Infrastructure) and the citation "% match" badge (Web) MUST use
// this helper — a divergent per-layer constant is exactly the 0–4-vs-0–1
// scale bug this type exists to prevent (see
// docs/superpowers/specs/2026-07-06-rag-relevance-floor-and-machine-scope-design.md).
// The semantic reranker (@search.rerankerScore) is documented 0.0–4.0;
// BM25-fallback scores are unbounded, so the result is clamped to [0,1].
public static class RetrievalScoring
{
    // Azure AI Search semantic reranker ceiling (@search.rerankerScore max).
    public const double MaxRerankerScore = 4.0;

    // Normalize a raw relevance score to a 0–1 fraction of the reranker
    // ceiling, clamped. The value equals the citation card's "% match" / 100.
    public static double NormalizeRerankerScore(double rawScore) =>
        Math.Clamp(rawScore / MaxRerankerScore, 0.0, 1.0);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "FullyQualifiedName~RetrievalScoring"`
Expected: PASS (8 cases).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Ai/Retrieval/RetrievalScoring.cs tests/PinballWizard.Application.Tests/Application/Ai/RetrievalScoringTests.cs
git commit -m "feat(rag) add shared RetrievalScoring reranker-score normalizer

Single 0-4 to 0-1 normalization the retriever floor and the citation
% match badge both delegate to, so the 0-4-vs-0-1 scale mismatch cannot
re-diverge across layers."
```

---

### Task 2: Point CitationCard at the shared helper + cross-layer parity test

**Files:**
- Modify: `src/PinballWizard.Web/Components/Citations/CitationCard.razor` (the `@code` block around lines 255–272)
- Test: `tests/PinballWizard.Web.Tests/Components/Citations/CitationCardTests.cs`

**Interfaces:**
- Consumes: `RetrievalScoring.NormalizeRerankerScore` (Task 1).
- Produces: `CitationCard.MatchPercent(double?)` unchanged in signature and output; the local `MaxRerankerScore` constant is removed.

- [ ] **Step 1: Confirm no external references to the local constant**

Run: `grep -rn "CitationCard.MaxRerankerScore\|\.MaxRerankerScore" src tests | grep -v RetrievalScoring`
Expected: no matches (nothing outside CitationCard reads it — safe to remove).

- [ ] **Step 2: Write the failing parity test**

Add this method inside the existing `CitationCardTests` class in `tests/PinballWizard.Web.Tests/Components/Citations/CitationCardTests.cs` (add `using PinballWizard.Application.Ai.Retrieval;` at the top if not present):

```csharp
// Cross-layer parity: the citation "% match" badge and the retriever's
// relevance floor must speak one scale. Both derive from the single
// RetrievalScoring.NormalizeRerankerScore helper; this pins that the UI
// percent equals round(sharedNormalize * 100), so the 0-4-vs-0-1 scale
// bug cannot re-emerge on the Web side.
[Theory]
[InlineData(1.12)]  // the 28% Cactus Canyon card
[InlineData(1.6)]
[InlineData(3.4)]
[InlineData(4.0)]
[InlineData(8.0)]
public void MatchPercent_equals_shared_normalization(double rerankerScore)
{
    var expected = (int)Math.Round(RetrievalScoring.NormalizeRerankerScore(rerankerScore) * 100.0);
    Assert.Equal(expected, CitationCard.MatchPercent(rerankerScore));
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~MatchPercent_equals_shared_normalization"`
Expected: FAIL — either compile error (`RetrievalScoring` not imported) or an assertion mismatch is impossible yet because MatchPercent still uses its own constant. (If it happens to pass because the math already agrees, that's fine — but the real guard is Step 4 making MatchPercent delegate. Proceed regardless.)

- [ ] **Step 4: Update CitationCard to delegate to the shared helper**

In `src/PinballWizard.Web/Components/Citations/CitationCard.razor`:

Add near the top of the file with the other `@using` directives:

```razor
@using PinballWizard.Application.Ai.Retrieval
```

Replace the block (lines ~255–272) that currently reads:

```csharp
    // RelevanceScore is the Azure AI Search semantic reranker score
    // (@search.rerankerScore), documented range 0.0–4.0 — NOT a 0–1 fraction.
    // The old `score * 100` rendered a typical reranker score of 1.9 as
    // "190% match". Normalize against the reranker ceiling to a true 0–100%
    // match, clamped so a rare BM25-fallback score (semantic ranker bypassed,
    // unbounded) cannot exceed 100%. Internal so CitationCardTests can assert
    // the mapping without rendering.
    internal const double MaxRerankerScore = 4.0;

    // A citation whose normalized match is >= this percent earns the amber
    // high-score accent (ADR-0026 § 4). 85% of the reranker ceiling is a 3.4
    // reranker score — a genuinely strong semantic match.
    internal const int HighMatchPercent = 85;

    internal static int? MatchPercent(double? relevanceScore) =>
        relevanceScore is double s
            ? Math.Clamp((int)Math.Round(s / MaxRerankerScore * 100.0), 0, 100)
            : null;
```

with:

```csharp
    // A citation whose normalized match is >= this percent earns the amber
    // high-score accent (ADR-0026 § 4). 85% of the reranker ceiling is a 3.4
    // reranker score — a genuinely strong semantic match.
    internal const int HighMatchPercent = 85;

    // The relevance value is the Azure semantic reranker score (0–4). It is
    // normalized to a 0–100% match via the shared RetrievalScoring helper —
    // the SAME normalization the retriever's minimum-score floor uses — so
    // the badge and the floor can never diverge (2026-07-06 design). The old
    // `score * 100` rendered a reranker score of 1.9 as "190% match".
    internal static int? MatchPercent(double? relevanceScore) =>
        relevanceScore is double s
            ? (int)Math.Round(RetrievalScoring.NormalizeRerankerScore(s) * 100.0)
            : null;
```

- [ ] **Step 5: Run the parity test AND the existing CitationCard tests to verify all pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~CitationCard"`
Expected: PASS — including the existing `Relevance_score_renders_as_normalized_percent` theory (unchanged behavior: 1.9→48%, 2.47→62%, 3.4→85%, 4.0→100%, 8.0→100%) and the new `MatchPercent_equals_shared_normalization`.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Citations/CitationCard.razor tests/PinballWizard.Web.Tests/Components/Citations/CitationCardTests.cs
git commit -m "refactor(web) citation % match delegates to shared RetrievalScoring

Removes CitationCard's private MaxRerankerScore constant; MatchPercent now
uses RetrievalScoring.NormalizeRerankerScore. Adds a cross-layer parity
test pinning the badge to the shared normalization."
```

---

### Task 3: Retriever normalizes the score before the minimum-score floor

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchRagRetriever.cs` (the filter at line ~91–95, and add a testable helper near `ResolveScore` at line ~324)
- Test: `tests/PinballWizard.Infrastructure.Tests/Rag/Retrieval/AiSearchRagRetrieverTests.cs`

**Interfaces:**
- Consumes: `RetrievalScoring.NormalizeRerankerScore` (Task 1).
- Produces: `internal static bool AiSearchRagRetriever.PassesMinimumScore(double rawScore, double minimumScore)` — true when the normalized score is `>=` the floor.

- [ ] **Step 1: Write the failing tests**

Add to `tests/PinballWizard.Infrastructure.Tests/Rag/Retrieval/AiSearchRagRetrieverTests.cs` (after the existing `ResolveScore_BothNullReturnsZero` test at line ~167). Note `using PinballWizard.Application.Ai.Retrieval;` may already be present via the retriever; add it if the file doesn't compile:

```csharp
    // ResolveScore returns the RAW reranker score (0–4), not a fraction. The
    // prior fixtures only used <=1.0 values, which never exercised the real
    // range — this documents that a genuine reranker score passes through.
    [Fact]
    public void ResolveScore_PassesThroughRerankerScoreAboveOne()
    {
        Assert.Equal(1.9, AiSearchRagRetriever.ResolveScore(rerankerScore: 1.9, bm25Score: 8.7));
        Assert.Equal(3.4, AiSearchRagRetriever.ResolveScore(rerankerScore: 3.4, bm25Score: null));
    }

    // The floor compares a NORMALIZED (0–1) score against MinimumScore. This
    // is the fixture where the filter actually fires: the 28%-match Cactus
    // Canyon junk (raw 1.12 → 0.28) is dropped at a 0.35 floor, while a
    // genuine 40%-match chunk (raw 1.6 → 0.40) survives.
    [Theory]
    [InlineData(1.12, 0.35, false)]  // 28% match — the incident junk — dropped
    [InlineData(1.6, 0.35, true)]    // 40% match — kept
    [InlineData(1.4, 0.35, true)]    // 35% match — exactly at the floor is kept (>=)
    [InlineData(0.0, 0.0, true)]     // floor 0.0 keeps everything (default posture)
    [InlineData(8.0, 0.35, true)]    // BM25 fallback clamps to 100% — kept
    public void PassesMinimumScore_ComparesNormalizedScore(double rawScore, double floor, bool expected) =>
        Assert.Equal(expected, AiSearchRagRetriever.PassesMinimumScore(rawScore, floor));
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~PassesMinimumScore|FullyQualifiedName~ResolveScore_PassesThrough"`
Expected: FAIL to compile — `PassesMinimumScore` does not exist.

- [ ] **Step 3: Add the helper and use it in the filter**

In `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchRagRetriever.cs`:

Add the helper immediately after the `ResolveScore(double?, double?)` overload (line ~325). Ensure `using PinballWizard.Application.Ai.Retrieval;` is in the file's usings:

```csharp
    // The minimum-score floor compares a NORMALIZED score. ResolveScore
    // returns the raw Azure reranker score (0–4) or a BM25 fallback; the
    // admin `rag.retrieval_minimum_score` key is a 0–1 fraction (== the
    // citation "% match" / 100). Normalizing here via the shared
    // RetrievalScoring helper is what lets a 0.35 floor mean "35% match"
    // and cut the low-relevance tail — before this, the 0–1 floor was
    // compared against a 0–4 score and could not fire (2026-07-06 design).
    internal static bool PassesMinimumScore(double rawScore, double minimumScore) =>
        RetrievalScoring.NormalizeRerankerScore(rawScore) >= minimumScore;
```

Then change the filter in `RetrieveAsync` (line ~91–95) from:

```csharp
                var score = ResolveScore(result);
                if (score < options.MinimumScore)
                {
                    continue;
                }

                chunks.Add(MapToChunk(result.Document, score));
```

to:

```csharp
                var score = ResolveScore(result);
                if (!PassesMinimumScore(score, options.MinimumScore))
                {
                    continue;
                }

                chunks.Add(MapToChunk(result.Document, score));
```

Note: `MapToChunk` still stores the RAW `score` (0–4) on the chunk — the citation layer normalizes for display, so nothing downstream changes. Only the filter decision is normalized.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~PassesMinimumScore|FullyQualifiedName~ResolveScore"`
Expected: PASS — the 3 existing ResolveScore tests, the new pass-through test, and the 5 PassesMinimumScore boundary cases.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchRagRetriever.cs tests/PinballWizard.Infrastructure.Tests/Rag/Retrieval/AiSearchRagRetrieverTests.cs
git commit -m "fix(rag) normalize reranker score before the minimum-score floor

The floor compared a 0-1 admin value against a 0-4 reranker score, so it
could not cut a 28%-match (score 1.12) result even at its max. Normalize
via the shared RetrievalScoring helper so the floor is a true % match.
Adds boundary tests exercising reranker scores >1.0 (the range the prior
fixtures never touched)."
```

---

### Task 4: Correct the WellKnownSettings scale documentation

**Files:**
- Modify: `src/PinballWizard.Application/Ai/Hosting/WellKnownSettings.cs` (comment at lines ~44–47)
- Test: `tests/PinballWizard.Application.Tests/Application/Ai/RuntimeSettingsTests.cs` (validation theory at lines ~146–150)

**Interfaces:**
- No code-behavior change. The `NumericRanges[RetrievalMinimumScore]` stays `(0.0, 1.0)` — now *correct* (a normalized fraction) rather than accidentally plausible. This task fixes the misleading comment and adds a test comment tying the range to the normalized scale.

- [ ] **Step 1: Update the comment**

In `src/PinballWizard.Application/Ai/Hosting/WellKnownSettings.cs`, replace the comment block at lines ~44–47:

```csharp
    //   retrieval_minimum_score: 0.0..1.0 — the semantic re-ranker and
    //     BM25 both produce scores in this range (ADR-0021 § Scoring).
    //     0.0 returns every hit; 1.0 would return almost nothing in
    //     practice. The calibration target from ADR-0023 H3 is ~0.5.
```

with:

```csharp
    //   retrieval_minimum_score: 0.0..1.0 — a NORMALIZED fraction of the
    //     reranker ceiling, equal to the citation "% match" / 100. The raw
    //     Azure semantic reranker score is 0–4 (RetrievalScoring.MaxRerankerScore);
    //     the retriever normalizes via RetrievalScoring.NormalizeRerankerScore
    //     before comparing to this floor, so 0.35 here means "cut anything
    //     below 35% match". 0.0 returns every hit; 1.0 keeps only a perfect
    //     match. Live default is 0.35 (2026-07-06 design); code default stays
    //     0.0 for CLI/fixtures.
```

- [ ] **Step 2: Add a clarifying test comment (no behavior change)**

In `tests/PinballWizard.Application.Tests/Application/Ai/RuntimeSettingsTests.cs`, the validation theory at lines ~146–150 already reads:

```csharp
    [InlineData("rag.retrieval_minimum_score", "0.0", true)]
    [InlineData("rag.retrieval_minimum_score", "0.5", true)]
    [InlineData("rag.retrieval_minimum_score", "1.0", true)]
    [InlineData("rag.retrieval_minimum_score", "-0.1", false)] // below floor
    [InlineData("rag.retrieval_minimum_score", "1.1", false)]  // above ceiling
```

Add a `0.35` accepted case (the live value) so the fixture documents intent:

```csharp
    [InlineData("rag.retrieval_minimum_score", "0.0", true)]
    [InlineData("rag.retrieval_minimum_score", "0.35", true)] // live default: 35% match floor
    [InlineData("rag.retrieval_minimum_score", "0.5", true)]
    [InlineData("rag.retrieval_minimum_score", "1.0", true)]  // 100% match — normalized ceiling
    [InlineData("rag.retrieval_minimum_score", "-0.1", false)] // below floor
    [InlineData("rag.retrieval_minimum_score", "1.1", false)]  // above ceiling
```

- [ ] **Step 3: Run the validation tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "FullyQualifiedName~RuntimeSettings"`
Expected: PASS (the new 0.35 row plus the existing cases).

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Application/Ai/Hosting/WellKnownSettings.cs tests/PinballWizard.Application.Tests/Application/Ai/RuntimeSettingsTests.cs
git commit -m "docs(rag) correct retrieval_minimum_score scale comment

The comment claimed the reranker score is 0-1; it is 0-4. Document that
the stored value is a normalized fraction (== % match / 100) that the
retriever applies after RetrievalScoring normalization. Adds a 0.35
accepted-value fixture row (the live default)."
```

---

### Task 5: Preserve machineId on the corpus-search retry (prompt)

**Files:**
- Modify: `src/PinballWizard.Application/Ai/Agents/Wizard.md` (Step 3, line ~37)

**Interfaces:** No code. This is a prompt (embedded `.md`) change. Its automated guard is the eval fixture in Task 6; the deterministic CI check is the ground-truth file test (Task 6, Step 5).

- [ ] **Step 1: Amend the retry instruction**

In `src/PinballWizard.Application/Ai/Agents/Wizard.md`, replace the Step 3 bullet at line 37:

```markdown
- If the first `searchCorpus` call returns empty and the routing table specifies a retry scope, call `searchCorpus` again with the retry `documentType`. De-duplicate hits from all calls by `document_url` (keep the first occurrence) before reasoning.
```

with:

```markdown
- If the first `searchCorpus` call returns empty and the routing table specifies a retry scope, call `searchCorpus` again with the retry `documentType` — and pass the **same `machineId` you used on the first call**. **Never drop the `machineId` on a retry:** a question that resolved to a specific machine must not silently widen into a corpus-wide search (that is how unrelated machines' records leak in). Only the indirect-reference routing row and genuinely machine-less questions issue an unscoped `searchCorpus`. De-duplicate hits from all calls by `document_url` (keep the first occurrence) before reasoning.
```

- [ ] **Step 2: Verify the prompt still builds as an embedded resource**

Run: `dotnet build src/PinballWizard.Application/PinballWizard.Application.csproj`
Expected: BUILD SUCCEEDED (the `.md` is an embedded resource; a build confirms it is still packaged).

- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Application/Ai/Agents/Wizard.md
git commit -m "fix(rag) Wizard retry must preserve machineId, not widen corpus-wide

A general-machine question whose manual search is empty retries against
metadata_card; the prompt did not say to keep the resolved machineId, so
the retry searched the whole corpus and surfaced unrelated machines
(Cactus Canyon incident). Make machineId retention explicit."
```

---

> **IMPLEMENTATION NOTE (2026-07-07):** Only the `machineId-filter-stability` fixture
> (`ev-rules-9002`, Cactus Canyon `G4835-Mb5eO`) shipped. The `reranker-sensitive` fixture
> (`ev-rules-9001`) was **intentionally deferred**: whether any question lands in the mid
> reranker-score band is unknowable without live eval score data, so it cannot be authored
> correctly until the eval-tuning step. Tracked as a follow-up (see Rollout §follow-ups). The
> ground-truth test asserts only the shipped slice.

### Task 6: Eval regression fixtures + ground-truth file coverage

**Files:**
- Modify: `data/eval/wizard.v2.jsonl` (append two rows)
- Test: `tests/PinballWizard.Infrastructure.Tests/Ai/Evaluation/EvalGroundTruthFileTests.cs`

**Interfaces:** Consumes the eval fixture schema (fields: `id`, `question`, `expected_sub_agent`, `expected_citation_set`, `acceptable_citation_sets`, `franchise_wide_ok`, `expected_outcome`, `acceptable_refusal`, `acceptable_sub_agents`, `slice`, `notes`).

- [ ] **Step 1: Read the current ground-truth file test to learn its assertion style**

Run: `sed -n '1,120p' tests/PinballWizard.Infrastructure.Tests/Ai/Evaluation/EvalGroundTruthFileTests.cs`
Note the parse helper it uses (it loads `data/eval/wizard.v2.jsonl` and asserts count / sub-agent membership / refusal-row emptiness). You will add one assertion in the same style.

- [ ] **Step 2: Add the reranker-sensitive fixture (uses a KNOWN, verified OPDB id)**

Append to `data/eval/wizard.v2.jsonl` (single line — Godzilla's OPDB id `GweeP-MW95j` is verified present in the existing fixture set at `ev-rules-0001`):

```json
{"id":"ev-rules-9001","question":"How does the Saw Blades feature work on Stern Godzilla?","expected_sub_agent":"Rules","expected_citation_set":["GweeP-MW95j"],"acceptable_citation_sets":[["GweeP-MW95j"],["GweeP-Ml9pZ"]],"franchise_wide_ok":true,"expected_outcome":"grounded","acceptable_refusal":false,"acceptable_sub_agents":["Wizard"],"slice":"reranker-sensitive","notes":"Reranker-sensitive slice (2026-07-06 design). A detailed sub-feature whose correct chunks tend to score in the 1.0-2.5 reranker range (25-62% match). Measures whether the 0.35 minimum-score floor cuts genuine low-relevance chunks. Re-check after any corpus expansion."}
```

- [ ] **Step 3: Resolve the real OPDB id for the machineId-filter-stability fixture — DO NOT GUESS**

The scope-stability fixture targets a title-collision machine (Cactus Canyon: Bally 1998 vs Chicago Gaming remake). Its `expected_citation_set` must be the machine's REAL OPDB id. Per the no-guessing rule, resolve it — do not invent it. Options in order of preference:

Run (queries the live OPDB catalog already synced to Cosmos; requires the live-load env per `reference_local_live_load_runbook`):
```bash
dotnet run --project src/PinballWizard.Cli -- --detect-title-collisions 2>&1 | grep -i "cactus"
```

Or query OPDB directly (token in machine env var `OPDB_API_TOKEN` per `reference_opdb_api_token`):
```bash
curl -s -H "Authorization: Bearer $OPDB_API_TOKEN" "https://opdb.org/api/search?q=Cactus%20Canyon" | jq '.[] | {id, name, manufacturer, year}'
```

Record the resolved id(s). If the machine is not in the corpus (no indexed documents), pick a *different* verified collision machine that IS in the corpus — the fixture is only meaningful if the correct answer has citable chunks.

- [ ] **Step 4: Add the machineId-filter-stability fixture (with the resolved id)**

Append to `data/eval/wizard.v2.jsonl`, substituting `RESOLVED-OPDB-ID` with the exact id captured in Step 3 (a single row; keep the machine name in the question matching the resolved machine):

```json
{"id":"ev-rules-9002","question":"Tell me about Cactus Canyon.","expected_sub_agent":"Rules","expected_citation_set":["RESOLVED-OPDB-ID"],"acceptable_citation_sets":[["RESOLVED-OPDB-ID"]],"franchise_wide_ok":true,"expected_outcome":"grounded","acceptable_refusal":false,"acceptable_sub_agents":["Wizard"],"slice":"machineId-filter-stability","notes":"Regression fixture for the 2026-07-06 machineId-drop incident. A general-machine question whose manual search is empty forces the metadata_card retry; if that retry drops machineId, the corpus-wide search returns OTHER machines' metadata cards (Attack from Mars, Alice Cooper) and precision collapses to 0. This row only passes when the retry preserves machineId. Title-collision case (Bally 1998 vs Chicago Gaming remake)."}
```

- [ ] **Step 5: Write the failing ground-truth assertion**

Add to `tests/PinballWizard.Infrastructure.Tests/Ai/Evaluation/EvalGroundTruthFileTests.cs` a test that both regression rows exist, are well-formed, and the scope-stability row has a non-empty machine-specific citation set. Match the file's existing parse-helper name (from Step 1) — the sketch below assumes a helper `LoadV2Questions()` returning parsed records with `Id`, `Slice`, `ExpectedCitationSet`; adapt property names to the actual parser:

```csharp
[Fact]
public void V2_ContainsRegressionSlicesFromThe20260706Design()
{
    var questions = LoadV2Questions();

    var scopeRow = Assert.Single(questions, q => q.Slice == "machineId-filter-stability");
    Assert.NotEmpty(scopeRow.ExpectedCitationSet); // machine-specific — a corpus-wide retry would fail this

    Assert.Contains(questions, q => q.Slice == "reranker-sensitive");
}
```

- [ ] **Step 6: Run test to verify it fails, then passes**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~EvalGroundTruth"`
Expected: initially FAIL if the assertion is added before the rows, then PASS once both rows are appended. (If the helper/property names differ, fix the test to match the real parser — confirmed in Step 1 — before declaring done.)

- [ ] **Step 7: Commit**

```bash
git add data/eval/wizard.v2.jsonl tests/PinballWizard.Infrastructure.Tests/Ai/Evaluation/EvalGroundTruthFileTests.cs
git commit -m "test(rag) add eval regression fixtures for scale + machineId scope

reranker-sensitive slice measures whether the 0.35 floor cuts genuine
low-relevance chunks. machineId-filter-stability slice turns the Cactus
Canyon incident into a permanent fixture: precision collapses to 0 if a
retry drops machineId. Ground-truth file test asserts both rows exist."
```

---

## Post-implementation verification (before PR)

- [ ] **Full CI-equivalent suite passes:**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: all green.

- [ ] **Behavioral verification (the `/verify` posture — observe the fix, not just tests):** run the eval harness against the new fixtures and confirm the scope-stability row grounds on the correct machine only. Requires the live-load env (`reference_local_live_load_runbook`) and confirmation before a live run (`feedback_confirm_before_live_ingestion_runs`):

Run: `dotnet run --project src/PinballWizard.Cli -- --eval` (confirm the "loaded N questions" log line names `wizard.v2.jsonl`, per `reference_eval_harness_config_keys`)
Expected: `machineId-filter-stability` row scores citation_precision 1.0 (only the resolved machine cited); no regression in aggregate citation_precision.

## Rollout (operational, after merge + deploy)

1. After the app is deployed, set the live relevance floor via the admin control plane: `rag.retrieval_minimum_score = 0.35` (runtime key — no second deploy).
2. Re-run `--eval`; confirm citation_precision holds and the Cactus Canyon query returns only Cactus Canyon records.
3. File the title-collision-clarification follow-up as a GitHub issue (non-goal of this PR — Step 2 of Wizard.md says to ask a clarifying question on unqualified collisions; that it answered instead is a separate gap).
4. File the `ToolCallTrace`-on-`WizardAnswer` follow-up (lets an evaluator grade tool arguments directly — see `reference_eval_harness_no_tool_trace`).
