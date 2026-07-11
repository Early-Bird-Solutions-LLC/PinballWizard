# Corpus Coverage Probe — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `--corpus-coverage` CLI verb + scheduled workflow that asserts, per (source × document_type) cell with ingested content, that ≥1 chunk is present and a query auto-derived from a sample chunk retrieves content from that same cell — proving every kind of content from every source is queryable, no LLM calls.

**Architecture:** An Application `RagSourceCatalog` maps each `IngestionSourceIds` source to a recognizer (manufacturer values + document_id prefix + machine_id sentinels). An `ICorpusCoverageProber` (Application) enumerates each source's live (source × doc-type) cells via an `ICorpusIndexQuery` port, and for each cell samples one chunk, builds a query from its title+heading, runs the existing `IRagRetriever`, and asserts a returned chunk matches the cell. The Infrastructure `AiSearchCorpusIndexQuery` implements the port over the AI Search `SearchClient` (built inline with `SharedAzureCredential`, mirroring `AiSearchRagCorpusStatsReader`). The CLI verb runs it against the live index, writes a `CoverageReport` JSON, and exits non-zero on gaps; a scheduled workflow runs the verb and opens/closes a pinned issue.

**Tech Stack:** .NET 10, Clean Architecture (Core/Application/Infrastructure/Cli), Azure.Search.Documents `SearchClient`, `System.CommandLine` (CLI), `System.Diagnostics.Metrics` (OTel), xUnit + NSubstitute, GitHub Actions.

## Global Constraints

- **Personal identity only.** Every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no Claude attribution trailer**.
- **Never on `main`.** Work happens on branch `feat/corpus-coverage-probe` in worktree `.worktrees/corpus-coverage-probe` (already created).
- **Clean Architecture.** Coverage domain types + prober live in Application (no Infrastructure reference); OData filter construction lives in Infrastructure. The `RagSource.Matches` predicate (chunk → source) is pure and lives in Application.
- **No masking (invariant #17).** A per-cell index/retrieval failure is recorded as `Retrievable=false` with an error note and the run continues — never a silent skip.
- **No LLM.** Coverage uses only index facet/count/sample + `IRagRetriever` (embedding + search). No agent/model calls.
- **No XML doc comments** on public surface (repo convention). Plain `//` comments only.
- **Tests assert behavior.** The prober tests use fixtures where a cell genuinely lacks retrievable content and assert it becomes a gap.
- **Source registry is authoritative & drift-guarded.** A contract test asserts `RagSourceCatalog` covers every `IngestionSourceIds` constant.
- **Live AI Search endpoint:** `https://pinwiz-search-dev-buutj.search.windows.net` (index `pinwiz-rag-v1`), verified from `tools/probe-findability-eval.csx`. Confirm against current Bicep output at workflow-verification time.
- **CI-equivalent test command:** `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.
- **All paths below are relative to the worktree root** `.worktrees/corpus-coverage-probe/`.

---

### Task 1: `RagSource` + `RagSourceCatalog` (source registry)

**Files:**
- Create: `src/PinballWizard.Application/Rag/Coverage/RagSource.cs`
- Create: `src/PinballWizard.Application/Rag/Coverage/RagSourceCatalog.cs`
- Create: `tests/PinballWizard.Application.Tests/Rag/Coverage/RagSourceCatalogTests.cs`

**Interfaces:**
- Produces: `RagSource` record (`SourceId`, `ManufacturerValues`, `DocumentIdPrefix`, `MachineIdSentinels`, `ExpectedNonEmpty`) with `bool Matches(string documentId, string manufacturer)`; `RagSourceCatalog.All : IReadOnlyList<RagSource>`.
- Consumes: `IngestionSourceIds` (Application), `SynthesizedSourceDescriptors` (Application).

- [ ] **Step 1: Write the `RagSource` record**

Create `src/PinballWizard.Application/Rag/Coverage/RagSource.cs`:

```csharp
namespace PinballWizard.Application.Rag.Coverage;

// One ingestion source and how to recognise its chunks in the RAG index.
// "Source" is NOT the same as the index `manufacturer` field: synthesized
// content (Kineticist/TiltForums/PB Freshdesk) carries the game's manufacturer,
// so those sources are identified by their document_id prefix instead. Scraped
// manufacturers are identified by manufacturer value AND the `doc_` prefix, so a
// Kineticist-for-Stern chunk (manufacturer="Stern", id="kineticist_…") is not
// misattributed to the Stern scraper.
public sealed record RagSource(
    string SourceId,
    IReadOnlyList<string> ManufacturerValues,
    string? DocumentIdPrefix,
    IReadOnlyList<string> MachineIdSentinels,
    bool ExpectedNonEmpty)
{
    // True when a retrieved chunk belongs to this source. Used to verify a
    // retrieval hit came from the cell under test.
    public bool Matches(string documentId, string manufacturer)
    {
        if (DocumentIdPrefix is not null &&
            !documentId.StartsWith(DocumentIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (ManufacturerValues.Count > 0 &&
            !ManufacturerValues.Contains(manufacturer, StringComparer.Ordinal))
        {
            return false;
        }

        return true;
    }
}
```

- [ ] **Step 2: Write the failing catalog + contract test**

Create `tests/PinballWizard.Application.Tests/Rag/Coverage/RagSourceCatalogTests.cs`:

```csharp
using System.Reflection;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Coverage;
using Xunit;

namespace PinballWizard.Application.Tests.Rag.Coverage;

public sealed class RagSourceCatalogTests
{
    // Drift guard: every IngestionSourceIds constant that produces RAG-indexed
    // content must have a RagSource, so adding a source without registering it
    // for coverage fails here. (PinballMap is data-only, not RAG-indexed — it is
    // the one documented exclusion.)
    [Fact]
    public void Catalog_CoversEveryIngestionSourceId_ExceptDocumentedExclusions()
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            IngestionSourceIds.PinballMap, // location data, never RAG-indexed
        };

        var allSourceIds = typeof(IngestionSourceIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .Where(id => !excluded.Contains(id))
            .ToHashSet(StringComparer.Ordinal);

        var covered = RagSourceCatalog.All.Select(s => s.SourceId).ToHashSet(StringComparer.Ordinal);

        var missing = allSourceIds.Except(covered).OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0, $"IngestionSourceIds missing from RagSourceCatalog: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Matches_SternScraper_ExcludesKineticistChunkWithSternManufacturer()
    {
        var stern = RagSourceCatalog.All.Single(s => s.SourceId == IngestionSourceIds.Stern);
        Assert.True(stern.Matches("doc_abc123", "Stern"));
        Assert.False(stern.Matches("kineticist_godzilla_GRBN", "Stern")); // synthesized, not scraped
    }

    [Fact]
    public void Matches_Kineticist_MatchesByPrefixRegardlessOfManufacturer()
    {
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == IngestionSourceIds.Kineticist);
        Assert.True(kin.Matches("kineticist_godzilla_GRBN", "Stern"));
        Assert.False(kin.Matches("doc_abc123", "Stern"));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~RagSourceCatalogTests"`
Expected: FAIL — `RagSourceCatalog` does not exist (compile error).

- [ ] **Step 4: Write `RagSourceCatalog`**

Create `src/PinballWizard.Application/Rag/Coverage/RagSourceCatalog.cs`:

```csharp
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Application.Rag.Coverage;

// Authoritative registry of RAG-indexed sources for the corpus-coverage probe.
// Kept next to IngestionSourceIds + SynthesizedSourceDescriptors; the contract
// test (RagSourceCatalogTests) fails if a new IngestionSourceId is added without
// a RagSource here. ExpectedNonEmpty=false marks sources that are wired but may
// legitimately have no content yet (their empty state is reported, not a gap).
public static class RagSourceCatalog
{
    private static readonly string[] None = [];

    public static readonly IReadOnlyList<RagSource> All =
    [
        // OPDB is not a scraped-doc source; its indexed content is the
        // per-machine synthesized metadata cards (meta_) and game overviews
        // (overview_). Represent those two synthesized classes as their own
        // sources keyed off the Opdb id.
        new(IngestionSourceIds.Opdb, None, "meta_", None, ExpectedNonEmpty: true),

        // Scraped manufacturers — manufacturer value AND the doc_ prefix.
        new(IngestionSourceIds.Stern, ["Stern"], "doc_", None, true),
        new(IngestionSourceIds.Jjp, ["Jersey Jack"], "doc_", None, true),
        new(IngestionSourceIds.JjpSupportDocs, ["Jersey Jack"], "doc_", None, false),
        new(IngestionSourceIds.Ap, ["American Pinball"], "doc_", None, true),
        new(IngestionSourceIds.ApBulletins, ["American Pinball"], "doc_", None, false),
        new(IngestionSourceIds.Spooky, ["Spooky", "Spooky Pinball"], "doc_", None, true),
        new(IngestionSourceIds.SpookySupport, ["Spooky", "Spooky Pinball"], "doc_", None, false),
        new(IngestionSourceIds.PinballBrothers, ["Pinball Brothers"], "doc_", None, true),
        new(IngestionSourceIds.PinballBrothersDocuments, ["Pinball Brothers"], "doc_", None, false),
        new(IngestionSourceIds.BarrelsOfFun, ["Barrels of Fun"], "doc_", None, false),
        new(IngestionSourceIds.Multimorphic, ["Multimorphic"], "doc_", None, false),
        new(IngestionSourceIds.Cgc, ["Chicago Gaming", "Chicago Gaming Company"], "doc_", None, true),

        // Synthesized sources — identified by document_id prefix only.
        new(IngestionSourceIds.Kineticist, None, SynthesizedSourceDescriptors.Kineticist.DocumentIdPrefix, None, true),
        new(IngestionSourceIds.TiltForumsRulesheets, None, SynthesizedSourceDescriptors.TiltForums.DocumentIdPrefix, None, true),
        new(IngestionSourceIds.Twip, None, SynthesizedSourceDescriptors.Twip.DocumentIdPrefix, ["pinball_news"], false),
        new(IngestionSourceIds.PinballBrothersFreshdesk, None, SynthesizedSourceDescriptors.PbFreshdesk.DocumentIdPrefix, ["pb_support"], false),
        new(IngestionSourceIds.MultimorphicP3Sdk, None, "p3sdk_", None, false),
    ];
}
```

> **Implementer note:** the exact `ManufacturerValues` strings must match the live index's `manufacturer` field values (from `Machine.ManufacturerDisplayName`). The values above are the expected display names; if the contract/parity work in Task 4's live dry-run shows a mismatch (e.g. `"Spooky Pinball"` vs `"Spooky"`), correct them here — the `Matches` test in Step 2 pins only Stern/Kineticist. This is data-dependent and is the one spot to reconcile against live at verification time.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~RagSourceCatalogTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
cd .worktrees/corpus-coverage-probe
git add src/PinballWizard.Application/Rag/Coverage/RagSource.cs \
        src/PinballWizard.Application/Rag/Coverage/RagSourceCatalog.cs \
        tests/PinballWizard.Application.Tests/Rag/Coverage/RagSourceCatalogTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(rag) RagSourceCatalog — source registry for corpus coverage"
```

---

### Task 2: Coverage ports + report types (Application)

**Files:**
- Create: `src/PinballWizard.Application/Rag/Coverage/ICorpusIndexQuery.cs`
- Create: `src/PinballWizard.Application/Rag/Coverage/CoverageReport.cs`

**Interfaces:**
- Produces: `ICorpusIndexQuery` (`CountAsync`, `FacetDocumentTypesAsync`, `SampleAsync`); `DocTypeCount`, `CorpusSample`, `CoverageCell`, `SourceFloor`, `CoverageReport` records.
- Consumes: `RagSource` (Task 1).

- [ ] **Step 1: Write the port + DTOs**

Create `src/PinballWizard.Application/Rag/Coverage/ICorpusIndexQuery.cs`:

```csharp
namespace PinballWizard.Application.Rag.Coverage;

// Read-side queries the coverage prober needs against the RAG index. The
// implementation (Infrastructure) translates a RagSource recognizer into an
// OData filter; the port keeps Application infra-free.
public interface ICorpusIndexQuery
{
    // Total indexed chunks matching the source's recognizer.
    Task<long> CountAsync(RagSource source, CancellationToken ct);

    // Distinct document_type values (with chunk counts) that have content for
    // this source — the live (source × doc-type) cells.
    Task<IReadOnlyList<DocTypeCount>> FacetDocumentTypesAsync(RagSource source, CancellationToken ct);

    // One sample chunk for a (source, document_type) cell, or null if none.
    Task<CorpusSample?> SampleAsync(RagSource source, string documentType, CancellationToken ct);
}

public sealed record DocTypeCount(string DocumentType, long ChunkCount);

public sealed record CorpusSample(
    string DocumentId,
    string Manufacturer,
    string DocumentType,
    string MachineTitle,
    string SectionHeading);
```

Create `src/PinballWizard.Application/Rag/Coverage/CoverageReport.cs`:

```csharp
namespace PinballWizard.Application.Rag.Coverage;

// A single (source × document_type) cell result.
public sealed record CoverageCell(
    string Source,
    string DocumentType,
    long ChunkCount,
    bool Retrievable,
    string SampleDocumentId,
    string Query,
    string? Error);

// A source-level presence result (the "source floor").
public sealed record SourceFloor(
    string Source,
    long ChunkCount,
    bool ExpectedNonEmpty,
    bool IsGap);

public sealed record CoverageReport(
    IReadOnlyList<CoverageCell> Cells,
    IReadOnlyList<SourceFloor> Sources,
    int CellsTotal,
    int CellsCovered,
    int GapsTotal)
{
    // A cell gap: a live cell whose content was not retrievable.
    public IEnumerable<CoverageCell> CellGaps => Cells.Where(c => !c.Retrievable);

    // A source-floor gap: an ExpectedNonEmpty source with zero chunks.
    public IEnumerable<SourceFloor> SourceGaps => Sources.Where(s => s.IsGap);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/PinballWizard.Application`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Application/Rag/Coverage/ICorpusIndexQuery.cs \
        src/PinballWizard.Application/Rag/Coverage/CoverageReport.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(rag) coverage port + report types (ICorpusIndexQuery, CoverageReport)"
```

---

### Task 3: `CorpusCoverageProber` (Application orchestrator)

**Files:**
- Create: `src/PinballWizard.Application/Rag/Coverage/ICorpusCoverageProber.cs`
- Create: `src/PinballWizard.Application/Rag/Coverage/CorpusCoverageProber.cs`
- Create: `tests/PinballWizard.Application.Tests/Rag/Coverage/CorpusCoverageProberTests.cs`

**Interfaces:**
- Consumes: `ICorpusIndexQuery` (Task 2), `RagSourceCatalog` (Task 1), `IRagRetriever` + `RetrievalOptions` + `RetrievedChunk` (Application `Ai/Retrieval`), `ILogger<CorpusCoverageProber>`.
- Produces: `ICorpusCoverageProber.RunAsync(CancellationToken) : Task<CoverageReport>`.

> **Consumed signatures (verbatim, already in the codebase):**
> - `IRagRetriever.RetrieveAsync(string queryText, RetrievalOptions options, CancellationToken ct) : Task<IReadOnlyList<RetrievedChunk>>`
> - `RetrievalOptions(int TopK = 10, string? MachineId = null, string? DocumentType = null, string? Manufacturer = null, double MinimumScore = 0.0)`
> - `RetrievedChunk` fields used: `DocumentId`, `Manufacturer`, `DocumentType` (all `string`).

- [ ] **Step 1: Write the interface**

Create `src/PinballWizard.Application/Rag/Coverage/ICorpusCoverageProber.cs`:

```csharp
namespace PinballWizard.Application.Rag.Coverage;

public interface ICorpusCoverageProber
{
    Task<CoverageReport> RunAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Write the failing prober tests**

Create `tests/PinballWizard.Application.Tests/Rag/Coverage/CorpusCoverageProberTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Rag.Coverage;
using Xunit;

namespace PinballWizard.Application.Tests.Rag.Coverage;

public sealed class CorpusCoverageProberTests
{
    private static RetrievedChunk Chunk(string documentId, string manufacturer, string docType) =>
        new(ChunkId: "c1", MachineId: "m1", MachineTitle: "T", Manufacturer: manufacturer,
            DocumentId: documentId, DocumentUrl: "u", DocumentType: docType,
            PageStart: 1, PageEnd: 1, SectionHeading: "H", Content: "x", Score: 1.0);

    [Fact]
    public async Task Cell_WhoseSampleContentIsRetrievable_IsCovered()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == "kineticist_tutorials");
        index.CountAsync(kin, Arg.Any<CancellationToken>()).Returns(5L);
        index.FacetDocumentTypesAsync(kin, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 5)]);
        index.SampleAsync(kin, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("kineticist_godzilla_GRBN", "Stern", "Rulesheet", "Godzilla", "Wizard Mode"));
        // Every other source: empty + not expected, so no gaps from them.
        index.CountAsync(Arg.Is<RagSource>(s => s != kin), Arg.Any<CancellationToken>()).Returns(0L);

        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns([Chunk("kineticist_godzilla_GRBN", "Stern", "Rulesheet")]);

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        var cell = report.Cells.Single(c => c.Source == "kineticist_tutorials" && c.DocumentType == "Rulesheet");
        Assert.True(cell.Retrievable);
        Assert.Equal("Godzilla Wizard Mode", cell.Query);
        Assert.Empty(report.CellGaps);
    }

    [Fact]
    public async Task Cell_WhoseContentIsNotInRetrieval_IsAGap()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == "kineticist_tutorials");
        index.CountAsync(kin, Arg.Any<CancellationToken>()).Returns(5L);
        index.FacetDocumentTypesAsync(kin, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 5)]);
        index.SampleAsync(kin, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("kineticist_godzilla_GRBN", "Stern", "Rulesheet", "Godzilla", "Wizard Mode"));
        index.CountAsync(Arg.Is<RagSource>(s => s != kin), Arg.Any<CancellationToken>()).Returns(0L);

        var retriever = Substitute.For<IRagRetriever>();
        // Retrieval returns a DIFFERENT source's chunk (a scraped doc_), not the Kineticist cell.
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns([Chunk("doc_other", "Stern", "Manual")]);

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        var cell = report.Cells.Single(c => c.Source == "kineticist_tutorials");
        Assert.False(cell.Retrievable);
        Assert.Contains(report.CellGaps, c => c.Source == "kineticist_tutorials");
    }

    [Fact]
    public async Task ExpectedNonEmptySource_WithZeroChunks_IsASourceGap()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        index.CountAsync(Arg.Any<RagSource>(), Arg.Any<CancellationToken>()).Returns(0L);
        var retriever = Substitute.For<IRagRetriever>();

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        Assert.Contains(report.SourceGaps, s => s.Source == "stern");        // ExpectedNonEmpty
        Assert.DoesNotContain(report.SourceGaps, s => s.Source == "twip");   // not ExpectedNonEmpty
    }

    [Fact]
    public async Task RetrievalThrows_RecordsCellAsNotRetrievable_WithError_DoesNotThrow()
    {
        var index = Substitute.For<ICorpusIndexQuery>();
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == "kineticist_tutorials");
        index.CountAsync(kin, Arg.Any<CancellationToken>()).Returns(5L);
        index.FacetDocumentTypesAsync(kin, Arg.Any<CancellationToken>())
             .Returns([new DocTypeCount("Rulesheet", 5)]);
        index.SampleAsync(kin, "Rulesheet", Arg.Any<CancellationToken>())
             .Returns(new CorpusSample("kineticist_x", "Stern", "Rulesheet", "Godzilla", "Wizard Mode"));
        index.CountAsync(Arg.Is<RagSource>(s => s != kin), Arg.Any<CancellationToken>()).Returns(0L);

        var retriever = Substitute.For<IRagRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                 .Returns<Task<IReadOnlyList<RetrievedChunk>>>(_ => throw new InvalidOperationException("search down"));

        var report = await BuildProber(index, retriever).RunAsync(CancellationToken.None);

        var cell = report.Cells.Single(c => c.Source == "kineticist_tutorials");
        Assert.False(cell.Retrievable);
        Assert.NotNull(cell.Error);
    }

    private static CorpusCoverageProber BuildProber(ICorpusIndexQuery index, IRagRetriever retriever) =>
        new(index, retriever, NullLogger<CorpusCoverageProber>.Instance);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~CorpusCoverageProberTests"`
Expected: FAIL — `CorpusCoverageProber` does not exist.

- [ ] **Step 4: Write the prober**

Create `src/PinballWizard.Application/Rag/Coverage/CorpusCoverageProber.cs`:

```csharp
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Retrieval;

namespace PinballWizard.Application.Rag.Coverage;

// Enumerates each source's live (source × doc-type) cells and, per cell, samples
// one chunk, builds a query from its title + section heading, runs the same
// IRagRetriever the Wizard uses, and asserts a returned chunk belongs to the cell.
// Presence + retrievability only — no LLM. A per-cell failure is recorded as a
// gap with an error note (no masking, invariant #17); the run still completes.
public sealed class CorpusCoverageProber : ICorpusCoverageProber
{
    private const int RetrievalTopK = 10;

    private readonly ICorpusIndexQuery _index;
    private readonly IRagRetriever _retriever;
    private readonly ILogger<CorpusCoverageProber> _logger;

    public CorpusCoverageProber(
        ICorpusIndexQuery index,
        IRagRetriever retriever,
        ILogger<CorpusCoverageProber> logger)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(logger);
        _index = index;
        _retriever = retriever;
        _logger = logger;
    }

    public async Task<CoverageReport> RunAsync(CancellationToken ct)
    {
        var cells = new List<CoverageCell>();
        var sources = new List<SourceFloor>();

        foreach (var source in RagSourceCatalog.All)
        {
            var count = await _index.CountAsync(source, ct).ConfigureAwait(false);
            var isGap = count == 0 && source.ExpectedNonEmpty;
            sources.Add(new SourceFloor(source.SourceId, count, source.ExpectedNonEmpty, isGap));

            if (count == 0)
            {
                if (isGap)
                {
                    _logger.LogWarning(
                        "Coverage source-floor gap: source={Source} has zero indexed chunks.", source.SourceId);
                }
                continue;
            }

            var docTypes = await _index.FacetDocumentTypesAsync(source, ct).ConfigureAwait(false);
            foreach (var dt in docTypes)
            {
                cells.Add(await ProbeCellAsync(source, dt, ct).ConfigureAwait(false));
            }
        }

        var covered = cells.Count(c => c.Retrievable);
        var gaps = cells.Count(c => !c.Retrievable) + sources.Count(s => s.IsGap);
        return new CoverageReport(cells, sources, cells.Count, covered, gaps);
    }

    private async Task<CoverageCell> ProbeCellAsync(RagSource source, DocTypeCount dt, CancellationToken ct)
    {
        var sample = await _index.SampleAsync(source, dt.DocumentType, ct).ConfigureAwait(false);
        if (sample is null)
        {
            return new CoverageCell(source.SourceId, dt.DocumentType, dt.ChunkCount,
                Retrievable: false, SampleDocumentId: string.Empty, Query: string.Empty,
                Error: "no sample chunk returned for cell");
        }

        var query = $"{sample.MachineTitle} {sample.SectionHeading}".Trim();
        try
        {
            var hits = await _retriever
                .RetrieveAsync(query, new RetrievalOptions(TopK: RetrievalTopK), ct)
                .ConfigureAwait(false);
            var retrievable = hits.Any(h =>
                source.Matches(h.DocumentId, h.Manufacturer) &&
                string.Equals(h.DocumentType, dt.DocumentType, StringComparison.Ordinal));
            return new CoverageCell(source.SourceId, dt.DocumentType, dt.ChunkCount,
                retrievable, sample.DocumentId, query, Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Coverage retrieval failed: source={Source} docType={DocType} query={Query}",
                source.SourceId, dt.DocumentType, query);
            return new CoverageCell(source.SourceId, dt.DocumentType, dt.ChunkCount,
                Retrievable: false, sample.DocumentId, query, Error: ex.Message);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~CorpusCoverageProberTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Rag/Coverage/ICorpusCoverageProber.cs \
        src/PinballWizard.Application/Rag/Coverage/CorpusCoverageProber.cs \
        tests/PinballWizard.Application.Tests/Rag/Coverage/CorpusCoverageProberTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(rag) CorpusCoverageProber — presence + retrievability per source×doc-type"
```

---

### Task 4: `AiSearchCorpusIndexQuery` (Infrastructure) + DI

**Files:**
- Create: `src/PinballWizard.Infrastructure/Rag/Coverage/AiSearchCorpusIndexQuery.cs`
- Create: `tests/PinballWizard.Infrastructure.Tests/Rag/Coverage/AiSearchCorpusIndexQueryFilterTests.cs`
- Modify: `src/PinballWizard.Infrastructure/Integrations/AiSearch/ServiceCollectionExtensions.cs` (register `ICorpusIndexQuery` in `AddAzureAiSearchIntegration`, after the `IRagRetriever` registration near line 90)

**Interfaces:**
- Consumes: `ICorpusIndexQuery`, `RagSource`, `DocTypeCount`, `CorpusSample` (Task 2); `AiSearchOptions`, `AiSearchIndexFields`, `RetrievedChunkDocument`, `SharedAzureCredential` (Infrastructure).
- Produces: `AiSearchCorpusIndexQuery : ICorpusIndexQuery` + `internal static string BuildSourceFilter(RagSource)`.

- [ ] **Step 1: Write the failing OData-filter test**

Create `tests/PinballWizard.Infrastructure.Tests/Rag/Coverage/AiSearchCorpusIndexQueryFilterTests.cs`:

```csharp
using PinballWizard.Application.Rag.Coverage;
using PinballWizard.Infrastructure.Rag.Coverage;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Coverage;

public sealed class AiSearchCorpusIndexQueryFilterTests
{
    [Fact]
    public void SourceFilter_ScrapedManufacturer_CombinesManufacturerAndDocPrefix()
    {
        var stern = new RagSource("stern", ["Stern"], "doc_", [], true);
        Assert.Equal(
            "(manufacturer eq 'Stern') and startswith(document_id, 'doc_')",
            AiSearchCorpusIndexQuery.BuildSourceFilter(stern));
    }

    [Fact]
    public void SourceFilter_MultipleManufacturerValues_OrsThem()
    {
        var spooky = new RagSource("spooky", ["Spooky", "Spooky Pinball"], "doc_", [], true);
        Assert.Equal(
            "(manufacturer eq 'Spooky' or manufacturer eq 'Spooky Pinball') and startswith(document_id, 'doc_')",
            AiSearchCorpusIndexQuery.BuildSourceFilter(spooky));
    }

    [Fact]
    public void SourceFilter_Kineticist_UsesPrefixOnly()
    {
        var kin = new RagSource("kineticist_tutorials", [], "kineticist_", [], true);
        Assert.Equal(
            "startswith(document_id, 'kineticist_')",
            AiSearchCorpusIndexQuery.BuildSourceFilter(kin));
    }

    [Fact]
    public void SourceFilter_EscapesApostropheInManufacturer()
    {
        var s = new RagSource("x", ["O'Brien"], "doc_", [], true);
        Assert.Equal(
            "(manufacturer eq 'O''Brien') and startswith(document_id, 'doc_')",
            AiSearchCorpusIndexQuery.BuildSourceFilter(s));
    }
}
```

> **OData note:** `document_id` is filterable, so `startswith(document_id, '<prefix>')` is the prefix predicate (no facet needed). Manufacturer single quotes are doubled (`'` → `''`), mirroring `AiSearchRagRetriever`'s OData escaping.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~AiSearchCorpusIndexQueryFilterTests"`
Expected: FAIL — `AiSearchCorpusIndexQuery` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/PinballWizard.Infrastructure/Rag/Coverage/AiSearchCorpusIndexQuery.cs`:

```csharp
using System.Text;
using Azure.Search.Documents;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Coverage;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Credentials;
using PinballWizard.Infrastructure.Rag.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Coverage;

// ICorpusIndexQuery over Azure AI Search. Builds its SearchClient inline from
// AiSearchOptions + SharedAzureCredential, mirroring AiSearchRagCorpusStatsReader.
// Translates a RagSource recognizer into an OData filter (manufacturer value(s)
// AND/OR document_id prefix); document_id is filterable (startswith), so a
// per-source facet on document_type yields that source's live cells.
public sealed class AiSearchCorpusIndexQuery : ICorpusIndexQuery
{
    private readonly AiSearchOptions _options;

    public AiSearchCorpusIndexQuery(IOptions<AiSearchOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    private SearchClient CreateClient()
    {
        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                $"Corpus coverage unavailable: {AiSearchOptions.EndpointKey} '{_options.Endpoint}' is not a valid absolute URL.");
        }
        return new SearchClient(endpoint, _options.IndexName, SharedAzureCredential.Instance);
    }

    public async Task<long> CountAsync(RagSource source, CancellationToken ct)
    {
        var response = await CreateClient().SearchAsync<RetrievedChunkDocument>(
            "*",
            new SearchOptions { Filter = BuildSourceFilter(source), IncludeTotalCount = true, Size = 0 },
            ct).ConfigureAwait(false);
        return response.Value.TotalCount ?? 0;
    }

    public async Task<IReadOnlyList<DocTypeCount>> FacetDocumentTypesAsync(RagSource source, CancellationToken ct)
    {
        var response = await CreateClient().SearchAsync<object>(
            "*",
            new SearchOptions
            {
                Filter = BuildSourceFilter(source),
                Size = 0,
                Facets = { $"{AiSearchIndexFields.DocumentType},count:30" },
            },
            ct).ConfigureAwait(false);

        var result = new List<DocTypeCount>();
        if (response.Value.Facets is { } facets &&
            facets.TryGetValue(AiSearchIndexFields.DocumentType, out var typeFacets))
        {
            foreach (var f in typeFacets)
            {
                var value = f.Value?.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(new DocTypeCount(value, f.Count ?? 0));
                }
            }
        }
        return result;
    }

    public async Task<CorpusSample?> SampleAsync(RagSource source, string documentType, CancellationToken ct)
    {
        var filter = $"{BuildSourceFilter(source)} and {AiSearchIndexFields.DocumentType} eq '{Escape(documentType)}'";
        var response = await CreateClient().SearchAsync<RetrievedChunkDocument>(
            "*",
            new SearchOptions
            {
                Filter = filter,
                Size = 1,
                Select =
                {
                    AiSearchIndexFields.DocumentId, AiSearchIndexFields.Manufacturer,
                    AiSearchIndexFields.DocumentType, AiSearchIndexFields.MachineTitle,
                    AiSearchIndexFields.SectionHeading,
                },
            },
            ct).ConfigureAwait(false);

        await foreach (var hit in response.Value.GetResultsAsync().ConfigureAwait(false))
        {
            var d = hit.Document;
            return new CorpusSample(d.DocumentId, d.Manufacturer, d.DocumentType, d.MachineTitle, d.SectionHeading);
        }
        return null;
    }

    // Recognizer → OData. manufacturer value(s) via equality OR-group; document_id
    // prefix via startswith. At least one clause is always present.
    internal static string BuildSourceFilter(RagSource source)
    {
        var clauses = new List<string>(2);

        if (source.ManufacturerValues.Count > 0)
        {
            var ors = source.ManufacturerValues
                .Select(m => $"{AiSearchIndexFields.Manufacturer} eq '{Escape(m)}'");
            clauses.Add($"({string.Join(" or ", ors)})");
        }

        if (source.DocumentIdPrefix is { } prefix)
        {
            clauses.Add($"startswith({AiSearchIndexFields.DocumentId}, '{Escape(prefix)}')");
        }

        return string.Join(" and ", clauses);
    }

    private static string Escape(string value) =>
        value.Contains('\'', StringComparison.Ordinal)
            ? value.Replace("'", "''", StringComparison.Ordinal)
            : value;
}
```

Update the filter test's exact-string assertion to match `BuildSourceFilter`'s real output:
`("manufacturer eq 'Stern') and startswith(document_id, 'doc_')` — adjust `SourceFilter_ScrapedManufacturer…` to `Assert.Equal("(manufacturer eq 'Stern') and startswith(document_id, 'doc_')", filter);`.

- [ ] **Step 4: Register in DI**

In `src/PinballWizard.Infrastructure/Integrations/AiSearch/ServiceCollectionExtensions.cs`, inside `AddAzureAiSearchIntegration`, after `services.TryAddSingleton<IRagRetriever>(BuildRagRetriever);`, add:

```csharp
    services.TryAddSingleton<PinballWizard.Application.Rag.Coverage.ICorpusIndexQuery,
        PinballWizard.Infrastructure.Rag.Coverage.AiSearchCorpusIndexQuery>();
    services.TryAddSingleton<PinballWizard.Application.Rag.Coverage.ICorpusCoverageProber,
        PinballWizard.Application.Rag.Coverage.CorpusCoverageProber>();
```

- [ ] **Step 5: Run tests + build**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~AiSearchCorpusIndexQueryFilterTests"` → PASS.
Run: `dotnet build src/PinballWizard.Infrastructure` → Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Rag/Coverage/AiSearchCorpusIndexQuery.cs \
        tests/PinballWizard.Infrastructure.Tests/Rag/Coverage/AiSearchCorpusIndexQueryFilterTests.cs \
        src/PinballWizard.Infrastructure/Integrations/AiSearch/ServiceCollectionExtensions.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(rag) AiSearchCorpusIndexQuery + DI for corpus coverage"
```

---

### Task 5: Telemetry counters

**Files:**
- Modify: `src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs` (add counters after the existing `AiMachineScopeGateShortCircuits` / `AiRefusals` declarations)

- [ ] **Step 1: Add the counters**

In `src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs`, add:

```csharp
    public static readonly Counter<long> RagCoverageCellsTotal = Meter.CreateCounter<long>(
        "pinwiz.rag.coverage.cells_total",
        unit: "{cell}",
        description: "Total (source × document_type) cells probed by the corpus-coverage run.");

    public static readonly Counter<long> RagCoverageCellsCovered = Meter.CreateCounter<long>(
        "pinwiz.rag.coverage.cells_covered",
        unit: "{cell}",
        description: "Cells whose sample content was retrievable in the corpus-coverage run.");

    public static readonly Counter<long> RagCoverageGaps = Meter.CreateCounter<long>(
        "pinwiz.rag.coverage.gaps_total",
        unit: "{gap}",
        description: "Corpus-coverage gaps: cells not retrievable, plus ExpectedNonEmpty sources with zero chunks.");
```

- [ ] **Step 2: Build**

Run: `dotnet build src/PinballWizard.Application` → Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(observability) corpus-coverage counters"
```

---

### Task 6: `--corpus-coverage` CLI verb

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs` (option declaration near the `evalOption` at line ~140; add to root command near line ~278; extract value near line ~319; handler block after the `--eval` handler near line ~1019)

**Interfaces:**
- Consumes: `ICorpusCoverageProber` (Task 3), `CoverageReport` (Task 2), `PinballWizardTelemetry` (Task 5), the `aiSearchWired` DI gate (already in `CreateHost`).

- [ ] **Step 1: Add the option, registration, and value extraction**

Near the `evalOption` declaration, add:

```csharp
var corpusCoverageOption = new Option<bool>("--corpus-coverage")
{
    Description = "Corpus coverage probe: for each (source × document_type) cell with indexed content, assert presence + retrievability (a query auto-derived from a sample chunk retrieves content from that cell). Writes data/eval/results/coverage.{ts}.json and exits non-zero on gaps. Requires AiSearch:Endpoint. No LLM calls."
};
```

After `rootCommand.Options.Add(evalOption);` add `rootCommand.Options.Add(corpusCoverageOption);`.
After `var eval = parseResult.GetValue(evalOption);` add `var corpusCoverage = parseResult.GetValue(corpusCoverageOption);`.

- [ ] **Step 2: Add the handler**

Immediately after the `--eval` handler's closing `}` (after its `return;`), add:

```csharp
    // Handle --corpus-coverage. Resolves ICorpusCoverageProber (registered only
    // when AddAzureAiSearchIntegration was wired, i.e. AiSearch:Endpoint is set).
    // Writes a timestamped CoverageReport JSON and exits non-zero on gaps so the
    // scheduled workflow can alarm. No Foundry/Cosmos required.
    if (corpusCoverage)
    {
        var prober = host.Services.GetService<ICorpusCoverageProber>();
        if (prober is null)
        {
            Console.Error.WriteLine(
                $"--corpus-coverage requires AI Search to be configured. Set {AiSearchOptions.EndpointKey}.");
            Environment.ExitCode = 2;
            return;
        }

        var report = await prober.RunAsync(cancellationToken);

        PinballWizardTelemetry.RagCoverageCellsTotal.Add(report.CellsTotal);
        PinballWizardTelemetry.RagCoverageCellsCovered.Add(report.CellsCovered);
        PinballWizardTelemetry.RagCoverageGaps.Add(report.GapsTotal);

        var resultsDir = Path.Combine("data", "eval", "results");
        Directory.CreateDirectory(resultsDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var path = Path.Combine(resultsDir, $"coverage.{stamp}.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Corpus coverage: {report.CellsCovered}/{report.CellsTotal} cells retrievable, " +
                          $"{report.GapsTotal} gaps. Report at {path}");
        foreach (var g in report.SourceGaps)
        {
            Console.WriteLine($"  SOURCE GAP: {g.Source} has zero indexed chunks (ExpectedNonEmpty).");
        }
        foreach (var g in report.CellGaps)
        {
            Console.WriteLine($"  CELL GAP: {g.Source} / {g.DocumentType} not retrievable" +
                              (g.Error is null ? "." : $" ({g.Error})."));
        }

        if (report.GapsTotal > 0)
        {
            Environment.ExitCode = 1;
        }
        return;
    }
```

Ensure the file's `using` directives include `PinballWizard.Application.Rag.Coverage;`, `PinballWizard.Application.Observability;` (for `PinballWizardTelemetry`), `System.Text.Json;`, `System.Globalization;` (check the top of Program.cs; add any missing).

- [ ] **Step 3: Build the CLI**

Run: `dotnet build src/PinballWizard.Cli`
Expected: Build succeeded.

- [ ] **Step 4: Smoke-run the verb without AI Search configured (exit code 2 path)**

Run: `dotnet run --project src/PinballWizard.Cli -- --corpus-coverage`
Expected: prints "requires AI Search to be configured", process exit code 2 (this proves the gate + wiring without needing live creds).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(cli) --corpus-coverage verb — writes CoverageReport, exits non-zero on gaps"
```

---

### Task 7: Scheduled workflow `corpus-coverage.yml`

**Files:**
- Create: `.github/workflows/corpus-coverage.yml`

- [ ] **Step 1: Write the workflow**

Create `.github/workflows/corpus-coverage.yml`:

```yaml
# Scheduled corpus-coverage probe against the live RAG index.
#
# Proves that every ingested (source × document_type) cell is present and
# retrievable — the coverage guarantee the eval harness (a curated regression
# floor) does not provide. No LLM calls: presence is a facet/count query,
# retrievability is one retrieval per cell. A gap opens/refreshes a pinned issue
# (honest-failure surfacing, invariant #17); a green run closes it.
#
# Auth: azure/login (OIDC, no long-lived secret), same federated credential as
# deploy.yml. The CLI's SharedAzureCredential resolves against the AI Search
# index; AZURE_TOKEN_CREDENTIALS=dev forces the developer chain (AzureCli, which
# azure/login populates) per the local live-load runbook.

name: Corpus coverage

on:
  schedule:
    - cron: '41 6 * * *' # daily, offset off top-of-hour
  workflow_dispatch: {}

permissions:
  contents: read
  issues: write
  id-token: write   # OIDC token exchange for azure/login

concurrency:
  group: corpus-coverage
  cancel-in-progress: false

jobs:
  coverage:
    name: Corpus coverage probe
    runs-on: ubuntu-latest
    timeout-minutes: 20
    env:
      DOTNET_NOLOGO: 'true'
      DOTNET_CLI_TELEMETRY_OPTOUT: 'true'
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 'true'

    steps:
      - name: Checkout
        uses: actions/checkout@v6

      - name: Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json

      - name: Cache NuGet packages
        uses: actions/cache@v5
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ hashFiles('**/packages.lock.json') }}
          restore-keys: |
            ${{ runner.os }}-nuget-

      - name: Azure login
        uses: azure/login@v3
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Run corpus-coverage probe
        env:
          AiSearch__Endpoint: https://pinwiz-search-dev-buutj.search.windows.net
          AZURE_TOKEN_CREDENTIALS: dev
        run: |
          dotnet run --project src/PinballWizard.Cli --configuration Release -- --corpus-coverage

      - name: Upload coverage report
        if: always()
        uses: actions/upload-artifact@v7
        with:
          name: coverage-report
          path: data/eval/results/coverage.*.json
          if-no-files-found: warn
          retention-days: 30

      - name: Raise the alarm
        if: failure()
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          title="Corpus coverage GAP against the deployed index"
          body="Run: ${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }} ($(date -u +%Y-%m-%dT%H:%M:%SZ))"
          existing=$(gh issue list --state open --search "\"$title\" in:title" --json number --jq '.[0].number // empty')
          if [ -n "$existing" ]; then
            gh issue comment "$existing" --body "Still gapping. $body"
          else
            alarm_body=$(printf 'The daily corpus-coverage probe found a gap: a (source × document_type) cell is not retrievable, or an expected source has zero indexed chunks. %s\n\nTriage: download the coverage-report artifact; each gap names the source + document_type. Closes automatically when a run goes green.' "$body")
            gh issue create --title "$title" --label bug --body "$alarm_body"
          fi

      - name: Close the alarm on green
        if: success()
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          title="Corpus coverage GAP against the deployed index"
          existing=$(gh issue list --state open --search "\"$title\" in:title" --json number --jq '.[0].number // empty')
          if [ -n "$existing" ]; then
            gh issue comment "$existing" --body "Coverage is back to full. Run: ${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }}"
            gh issue close "$existing"
          fi
```

- [ ] **Step 2: Validate the YAML**

Run: `python -c "import yaml; yaml.safe_load(open('.github/workflows/corpus-coverage.yml')); print('valid')"`
Expected: `valid`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/corpus-coverage.yml
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "ci(coverage) scheduled corpus-coverage workflow — probe live index, alarm on gaps"
```

---

### Task 8: Full-suite verification + flip spec status + post-merge live dry-run

**Files:**
- Modify: `docs/superpowers/specs/2026-07-11-corpus-coverage-probe-design.md` (Status: Approved → Implemented)

- [ ] **Step 1: Run the full CI-equivalent suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: PASS (all projects). Fix any failure at root cause; do not adjust filters.

- [ ] **Step 2: Zero-warning build**

Run: `dotnet build PinballWizard.slnx --nologo -warnaserror`
Expected: 0 warnings / 0 errors.

- [ ] **Step 3: Flip spec status + commit**

Change the spec's `**Status:** Approved (design) — implementation plan to follow` to `**Status:** Implemented`.

```bash
git add docs/superpowers/specs/2026-07-11-corpus-coverage-probe-design.md
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "docs(spec) corpus coverage probe — implemented"
```

- [ ] **Step 4: Pre-push self-audit**

Run `/local-review` and `/standards-audit` against the branch diff; treat 🔴 as blocking; record outcomes in the PR description.

- [ ] **Step 5 (POST-MERGE, live verification — cannot run in CI):**

After the PR merges, trigger the workflow once via `workflow_dispatch` (`gh workflow run "Corpus coverage" --ref main`) and watch it. This is the first time the CLI runs against the live index with real credentials. Verify:
1. `azure/login` + `AZURE_TOKEN_CREDENTIALS=dev` resolves creds (the probe reaches the index; if not, mirror `reference_local_live_load_runbook` — the auth env is the one empirically-verified item flagged in this plan).
2. The coverage report artifact uploads and lists cells per source.
3. Reconcile any `RagSourceCatalog` `ManufacturerValues` that don't match live `manufacturer` values (Task 1 Step 4 note) — a source showing 0 chunks when it should have content usually means a manufacturer-string mismatch. Fix in `RagSourceCatalog`, push, re-dispatch.
4. Confirm a genuine gap opens the pinned issue and a green run closes it.

---

## Verification summary (what "done" looks like)

- Contract test proves `RagSourceCatalog` covers every RAG-indexed `IngestionSourceIds` (drift guard).
- Prober unit tests prove: retrievable cell → covered; unretrievable cell → gap; ExpectedNonEmpty empty source → gap; retrieval exception → gap-with-error, run continues (no masking).
- OData filter test pins the recognizer→filter translation.
- CLI verb writes `coverage.{ts}.json`, prints a summary, exits non-zero on gaps; the no-AI-Search path exits 2.
- Scheduled workflow runs the verb against the live index, uploads the report, and opens/closes a pinned issue on gap/green.
- Full suite green; `-warnaserror` clean.
- Post-merge `workflow_dispatch` dry-run confirms live auth + real per-source coverage (the one empirically-verified step).

## Notes / deferred (per spec "out of scope")

- Per-document exhaustive presence; full-Wizard answerability per cell; curated per-cell query overrides — all deferred. Auto-derived query + (source × doc-type) grain is the shipped scope.
- If the auto-derived query proves a poor proxy for a specific cell during the live dry-run, add an optional `QueryOverride` to `RagSource` and prefer it in `ProbeCellAsync` — a small, isolated follow-up.
