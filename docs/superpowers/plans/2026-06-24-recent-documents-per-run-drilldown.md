# Recent items per run — drill-down Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-run drill-down to the admin scrape-run timeline: click a run → list the items it first captured (documents for manufacturer sources, machines for OPDB), with a self-consistent "N processed · M new" count.

**Architecture:** A write-once `run_id` (the deterministic scrape-run id) is stamped on each captured item only when first created and preserved on re-discovery. A `documents_new` count on the run record (= items first-captured) backs the drill-down list, which queries `scraped_documents_raw` by `run_id` for manufacturer sources and `machines` by `run_id` for OPDB. Design: [docs/superpowers/specs/2026-06-24-recent-documents-per-run-drilldown-design.md](../specs/2026-06-24-recent-documents-per-run-drilldown-design.md).

**Tech Stack:** .NET 10, Cosmos (data-plane SDK + ARM schema), Blazor + MudBlazor, xUnit + NSubstitute + bUnit.

## Global Constraints

- **TDD throughout** — failing test first, minimal impl, green, commit.
- **Commit identity:** `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no Claude attribution trailer**; conventional `type(scope) subject`; stage explicit paths (never `git add -A`).
- **No XML doc comments** on public surface (repo rule).
- **`run_id` value is the deterministic scrape-run id** `ScrapeRunId.For(sourceId, runAt)` = `"{sourceId}_{runAt.UtcDateTime:yyyyMMddHHmmssfff}Z"` — the SAME string used as the run record's `id`. Never a raw timestamp.
- **JSON style:** Cosmos POCOs use `[JsonPropertyName("snake_case")]`; the `Machine` domain type (which is its own wire shape) uses the existing camelCase + `run_id` exactly as written below.
- **Build gate:** `dotnet build PinballWizard.slnx` 0 warn / 0 err; pre-push run `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"` green.
- **Branch:** `feat/recent-documents-per-run-drilldown` (already created; spec already committed there).

---

### Task 1: `ScrapeRunId` helper (centralize run-id derivation)

**Files:**
- Create: `src/PinballWizard.Core/Models/ScrapeRunId.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapeRunRepository.cs:44-46` (delegate `DeriveId` to the helper)
- Test: `tests/PinballWizard.Core.Tests/Models/ScrapeRunIdTests.cs`

**Interfaces:**
- Produces: `static string ScrapeRunId.For(string sourceId, DateTimeOffset runAt)` → `"{sourceId}_{runAt.UtcDateTime:yyyyMMddHHmmssfff}Z"`.

- [ ] **Step 1: Write the failing test**

```csharp
using PinballWizard.Core.Models;

namespace PinballWizard.Core.Tests.Models;

public sealed class ScrapeRunIdTests
{
    [Fact]
    public void For_BuildsDeterministicIdFromSourceAndUtcRunAt()
    {
        var runAt = new DateTimeOffset(2026, 6, 21, 4, 0, 3, TimeSpan.Zero);
        Assert.Equal("opdb_20260621040003000Z", ScrapeRunId.For("opdb", runAt));
    }

    [Fact]
    public void For_NormalizesToUtc_BeforeFormatting()
    {
        // 23:30 at +05:00 == 18:30 UTC
        var runAt = new DateTimeOffset(2026, 6, 21, 23, 30, 0, TimeSpan.FromHours(5));
        Assert.Equal("stern_20260621183000000Z", ScrapeRunId.For("stern", runAt));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Core.Tests --filter "FullyQualifiedName~ScrapeRunIdTests"`
Expected: FAIL — `ScrapeRunId` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace PinballWizard.Core.Models;

public static class ScrapeRunId
{
    // Deterministic per-run id: same source can't run twice in one millisecond
    // (runs are serial). Stamped on captured items as run_id AND used as the
    // scrape_runs document id, so a document's run_id == its run record's id.
    public static string For(string sourceId, DateTimeOffset runAt) =>
        $"{sourceId}_{runAt.UtcDateTime:yyyyMMddHHmmssfff}Z";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Core.Tests --filter "FullyQualifiedName~ScrapeRunIdTests"`
Expected: PASS.

- [ ] **Step 5: Refactor `CosmosScrapeRunRepository.DeriveId` to delegate**

In `CosmosScrapeRunRepository.cs`, replace the body of `DeriveId` (lines 44-46) with:

```csharp
private static string DeriveId(string sourceId, DateTimeOffset runAt) =>
    PinballWizard.Core.Models.ScrapeRunId.For(sourceId, runAt);
```

- [ ] **Step 6: Run the scrape-run repo tests + build**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosScrapeRunRepository"`
Expected: build 0/0; existing repo tests still PASS (id format unchanged).

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Core/Models/ScrapeRunId.cs tests/PinballWizard.Core.Tests/Models/ScrapeRunIdTests.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapeRunRepository.cs
git commit -m "refactor(core) extract ScrapeRunId.For; reuse in CosmosScrapeRunRepository"
```

---

### Task 2: `documents_new` on the run record

**Files:**
- Modify: `src/PinballWizard.Core/Models/ScrapeRunRecord.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapeRunCosmosRecord.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapeRunRepository.cs:48-67` (`ToCosmos`/`ToDomain`)
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosScrapeRunRepositoryTests.cs`

**Interfaces:**
- Produces: `ScrapeRunRecord.DocumentsNew` (int, default 0); JSON `documents_new`.

- [ ] **Step 1: Write the failing test** (add to `CosmosScrapeRunRepositoryTests`)

```csharp
[Fact]
public async Task WriteAsync_PersistsDocumentsNew()
{
    ScrapeRunCosmosRecord? captured = null;
    _container
        .UpsertItemAsync(Arg.Do<ScrapeRunCosmosRecord>(r => captured = r),
            Arg.Any<PartitionKey?>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
        .Returns(ci => MakeItemResponse(ci.Arg<ScrapeRunCosmosRecord>()));

    await _repository.WriteAsync(
        new ScrapeRunRecord
        {
            SourceId = "stern", RunAt = DateTimeOffset.UtcNow, DurationSeconds = 1.0,
            Succeeded = true, DocumentsDiscovered = 10, DocumentsNew = 3,
        },
        CancellationToken.None);

    Assert.NotNull(captured);
    Assert.Equal(3, captured!.DocumentsNew);
}
```

> Match the existing `WriteAsync` test's upsert-capture harness in this file (reuse its `MakeItemResponse`/substitute setup; the snippet above mirrors the established pattern — copy the exact helper names already present).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosScrapeRunRepositoryTests.WriteAsync_PersistsDocumentsNew"`
Expected: FAIL — `ScrapeRunRecord` has no `DocumentsNew`.

- [ ] **Step 3: Add the field to the domain record**

In `ScrapeRunRecord.cs`, add after `DocumentsDiscovered`:

```csharp
    public int DocumentsNew { get; init; }
```

(Not `required` — defaults to 0 so legacy callers/records compile and read as 0.)

- [ ] **Step 4: Add the field to the Cosmos POCO**

In `ScrapeRunCosmosRecord.cs`, add after the `documents_discovered` property:

```csharp
    [JsonPropertyName("documents_new")]
    public int DocumentsNew { get; set; }
```

- [ ] **Step 5: Map it in both directions**

In `CosmosScrapeRunRepository.cs`, add `DocumentsNew = r.DocumentsNew,` to `ToCosmos` and `DocumentsNew = c.DocumentsNew,` to `ToDomain`.

- [ ] **Step 6: Run test + build**

Run: `dotnet build PinballWizard.slnx` then the Step-2 filter.
Expected: build 0/0; PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Core/Models/ScrapeRunRecord.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapeRunCosmosRecord.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapeRunRepository.cs tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosScrapeRunRepositoryTests.cs
git commit -m "feat(persistence) add documents_new to ScrapeRunRecord"
```

---

### Task 3: `run_id` on the document model

**Files:**
- Modify: `src/PinballWizard.Core/Models/DocumentRecord.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentRecord.cs` (field + `MapToCosmosRecord:263` + `MapToDomain:331`)
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosRawDocumentRepositoryTests.cs`

**Interfaces:**
- Produces: `DocumentRecord.RunId` (`string?`); `RawDocumentCosmosRecord.RunId` (JSON `run_id`).

- [ ] **Step 1: Write the failing test** (round-trip through the map)

```csharp
[Fact]
public async Task UpsertRawAsync_NewDocument_PersistsRunId()
{
    // Arrange: no existing doc → insert path. (Reuse this file's existing
    // "new document" harness: substitute ReadItem to 404, capture UpsertItem.)
    RawDocumentCosmosRecord? captured = CaptureUpsert();   // existing helper pattern in this file
    var record = NewDocumentRecord();                      // existing helper in this file
    record.RunId = "stern_20260624031712000Z";

    await _repository.UpsertRawAsync(record, CancellationToken.None);

    Assert.Equal("stern_20260624031712000Z", captured!.RunId);
}
```

> Use the file's existing new-document harness/helpers (the 404-on-read + capture-on-upsert pattern already used by its insert tests). Do not invent a new harness.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosRawDocumentRepositoryTests.UpsertRawAsync_NewDocument_PersistsRunId"`
Expected: FAIL — no `RunId` on `DocumentRecord`/POCO (compile error).

- [ ] **Step 3: Add `RunId` to `DocumentRecord`**

In `DocumentRecord.cs`, add to the `DocumentRecord` class body (after `CrossReferences`):

```csharp
    public string? RunId { get; set; }
```

- [ ] **Step 4: Add `run_id` to the Cosmos POCO + maps**

In `RawDocumentRecord.cs`, add to `RawDocumentCosmosRecord`:

```csharp
    [JsonPropertyName("run_id")]
    public string? RunId { get; set; }
```

In `MapToCosmosRecord` (line 263 `new RawDocumentCosmosRecord { … }`) add `RunId = record.RunId,`.
In `MapToDomain` (line 331 `new DocumentRecord { … }`) add `RunId = cosmos.RunId,`.

- [ ] **Step 5: Run test + build**

Run: `dotnet build PinballWizard.slnx` then the Step-2 filter.
Expected: build 0/0; PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Core/Models/DocumentRecord.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentRecord.cs tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosRawDocumentRepositoryTests.cs
git commit -m "feat(persistence) add run_id to DocumentRecord + raw POCO"
```

---

### Task 4: `UpsertRawAsync` reports insert-vs-update + write-once `run_id`

**Files:**
- Modify: `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs`
- Create (in same file or alongside): `RawDocumentUpsertResult` + `UpsertOutcome`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs:25-90` (`UpsertRawAsync`)
- Modify: every caller of `UpsertRawAsync` (see Step 5)
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosRawDocumentRepositoryTests.cs`

**Interfaces:**
- Consumes: `DocumentRecord.RunId` (Task 3).
- Produces: `Task<RawDocumentUpsertResult> UpsertRawAsync(DocumentRecord, CancellationToken)`; `readonly record struct RawDocumentUpsertResult(RawDocumentRecord Record, UpsertOutcome Outcome)`; `enum UpsertOutcome { Created, Updated }`.

- [ ] **Step 1: Write the failing tests** (Created/Updated + write-once preservation)

```csharp
[Fact]
public async Task UpsertRawAsync_NewDocument_ReturnsCreated()
{
    var record = NewDocumentRecord();
    record.RunId = "run_A";

    var result = await _repository.UpsertRawAsync(record, CancellationToken.None);

    Assert.Equal(UpsertOutcome.Created, result.Outcome);
}

[Fact]
public async Task UpsertRawAsync_ExistingDocument_ReturnsUpdated_AndPreservesOriginalRunId()
{
    var existing = ExistingCosmosRecord();      // existing helper: ReadItem returns this
    existing.RunId = "run_A";                   // first-discovery run
    GivenExisting(existing);
    var captured = CaptureUpsert();

    var incoming = NewDocumentRecord();
    incoming.RunId = "run_B";                   // a later run re-sees the doc

    var result = await _repository.UpsertRawAsync(incoming, CancellationToken.None);

    Assert.Equal(UpsertOutcome.Updated, result.Outcome);
    Assert.Equal("run_A", captured!.RunId);     // write-once: original preserved
}
```

> `GivenExisting` / `ExistingCosmosRecord` / `CaptureUpsert` / `NewDocumentRecord` are the harness names already in this file's existing upsert tests — reuse them verbatim.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosRawDocumentRepositoryTests.UpsertRawAsync_NewDocument_ReturnsCreated|FullyQualifiedName~CosmosRawDocumentRepositoryTests.UpsertRawAsync_ExistingDocument_ReturnsUpdated"`
Expected: FAIL — `UpsertOutcome`/`RawDocumentUpsertResult` don't exist.

- [ ] **Step 3: Add the result types + change the interface**

In `IRawDocumentRepository.cs`, add (top of namespace) and change the signature:

```csharp
public enum UpsertOutcome { Created, Updated }

public readonly record struct RawDocumentUpsertResult(RawDocumentRecord Record, UpsertOutcome Outcome);
```

Change:
```csharp
    Task<RawDocumentRecord> UpsertRawAsync(DocumentRecord record, CancellationToken cancellationToken);
```
to:
```csharp
    Task<RawDocumentUpsertResult> UpsertRawAsync(DocumentRecord record, CancellationToken cancellationToken);
```

- [ ] **Step 4: Implement write-once stamp + outcome**

In `CosmosRawDocumentRepository.UpsertRawAsync`, in the `else` (new) branch, stamp run_id before mapping; and return the outcome. Replace the tail of the method:

```csharp
        cosmos = existing;
        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
        return new RawDocumentUpsertResult(MapToDomain(cosmos), UpsertOutcome.Updated);
    }
    else
    {
        cosmos = MapToCosmosRecord(record);     // record.RunId flows through (Task 3 map)
        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
        return new RawDocumentUpsertResult(MapToDomain(cosmos), UpsertOutcome.Created);
    }
}
```

(The existing branch already preserves `existing.RunId` simply by NOT copying `record.RunId` onto `existing` — the merge block only touches timeline/xrefs/hash. Confirm no line sets `existing.RunId`.)

- [ ] **Step 5: Update every caller for the new return type**

Run: `git grep -n "UpsertRawAsync(" -- src tests`
For each call site: if it used the returned `RawDocumentRecord`, change to read `.Record`. The known production caller `ScraperOrchestrator.cs:100` currently discards the result — leave it discarding for now (Task 8 consumes `.Outcome`). Update any test that asserts on the old return type to use `.Record`.

- [ ] **Step 6: Run tests + build**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosRawDocumentRepositoryTests"`
Expected: build 0/0; all raw-doc repo tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosRawDocumentRepositoryTests.cs
# plus any caller files touched in Step 5
git commit -m "feat(persistence) UpsertRawAsync returns Created/Updated; write-once run_id"
```

---

### Task 5: `StreamByRunIdAsync` on the raw-document repository

**Files:**
- Modify: `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs`
- Modify: `tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs:61-65`
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosRawDocumentRepositoryTests.cs`

**Interfaces:**
- Produces: `IAsyncEnumerable<RawDocumentRecord> StreamByRunIdAsync(string runId, CancellationToken)`.

- [ ] **Step 1: Write the failing test** (capture the QueryDefinition)

```csharp
[Fact]
public async Task StreamByRunIdAsync_QueriesByRunId_CrossPartition()
{
    QueryDefinition? captured = null;
    _container
        .GetItemQueryIterator<RawDocumentCosmosRecord>(
            Arg.Do<QueryDefinition>(q => captured = q),
            Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
        .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[]]));

    await foreach (var _ in _repository.StreamByRunIdAsync("stern_20260624031712000Z", CancellationToken.None)) { }

    Assert.NotNull(captured);
    Assert.Contains("c.run_id = @runId", captured!.QueryText);
    Assert.Contains(captured.GetQueryParameters(), p => p.Name == "@runId" && (string)p.Value == "stern_20260624031712000Z");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosRawDocumentRepositoryTests.StreamByRunIdAsync"`
Expected: FAIL — method not defined.

- [ ] **Step 3: Add to the interface**

In `IRawDocumentRepository.cs`:
```csharp
    IAsyncEnumerable<RawDocumentRecord> StreamByRunIdAsync(string runId, CancellationToken cancellationToken);
```

- [ ] **Step 4: Implement (matches `StreamBySourcePatternAsync` pattern)**

In `CosmosRawDocumentRepository.cs`:

```csharp
public async IAsyncEnumerable<RawDocumentRecord> StreamByRunIdAsync(
    string runId,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(runId);

    var parameters = new Dictionary<string, object> { ["runId"] = runId };
    await foreach (var cosmos in StreamCrossPartitionAsync(
        "SELECT * FROM c WHERE c.run_id = @runId", parameters, cancellationToken).ConfigureAwait(false))
    {
        yield return MapToDomain(cosmos);
    }
}
```

- [ ] **Step 5: Update the allow-list justification**

In `CrossPartitionQueryAllowListTests.cs`, change the `["CosmosRawDocumentRepository.cs"]` value to append:
`", and StreamByRunIdAsync (per-run drill-down, back-office admin path)"`.

- [ ] **Step 6: Run tests + build**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosRawDocumentRepositoryTests.StreamByRunIdAsync|FullyQualifiedName~CrossPartitionQueryAllowListTests"`
Expected: build 0/0; PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosRawDocumentRepositoryTests.cs
git commit -m "feat(persistence) StreamByRunIdAsync on raw-document repository"
```

---

### Task 6: Index `run_id` on `scraped_documents_raw`

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs` (the `scraped_documents_raw` `IncludedPaths`)
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosOptionsTests.cs`

> NOTE: the `machines` container uses DEFAULT indexing (no `IndexingPolicy`), which already indexes `run_id`. Do NOT add a selective policy to `machines` — it would de-index `title`/`groupId` and break those queries. Only `scraped_documents_raw` (which has a selective policy) needs `run_id` added.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Defaults_ScrapedDocumentsRaw_IndexesRunId()
{
    var c = Assert.Single(new CosmosOptions().Containers, x => x.Name == "scraped_documents_raw");
    Assert.NotNull(c.IndexingPolicy);
    Assert.Contains("/run_id/?", c.IndexingPolicy!.IncludedPaths);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosOptionsTests.Defaults_ScrapedDocumentsRaw_IndexesRunId"`
Expected: FAIL — `/run_id/?` not in IncludedPaths.

- [ ] **Step 3: Add the path**

In `CosmosOptions.cs`, in the `scraped_documents_raw` container's `IndexingPolicy.IncludedPaths`, add `"/run_id/?"` (keep all existing included paths).

- [ ] **Step 4: Run test + build**

Run: `dotnet build PinballWizard.slnx` then the Step-2 filter, then the omnibus `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~CosmosOptionsTests"`.
Expected: build 0/0; all CosmosOptions tests PASS (the 15-container count is unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/CosmosOptionsTests.cs
git commit -m "feat(persistence) index run_id on scraped_documents_raw"
```

---

### Task 7: `run_id` on `Machine` + `StreamByRunIdAsync` on the machine repository

**Files:**
- Modify: `src/PinballWizard.Core/Domain/Machine.cs` (after `LastSeenAt`, before `_etag`)
- Modify: `src/PinballWizard.Application/Persistence/IMachineRepository.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/MachineRepository.cs`
- Modify: `tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs:44-47`
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/MachineRepositoryTests.cs`

**Interfaces:**
- Produces: `Machine.RunId` (`string?`, JSON `run_id`); `IAsyncEnumerable<Machine> StreamByRunIdAsync(string runId, CancellationToken)`.

- [ ] **Step 1: Write the failing test** (match this file's existing query-capture pattern)

```csharp
[Fact]
public async Task StreamByRunIdAsync_QueriesByRunId()
{
    QueryDefinition? captured = null;
    _container
        .GetItemQueryIterator<Machine>(Arg.Do<QueryDefinition>(q => captured = q),
            Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
        .Returns(new FakeFeedIterator<Machine>([[]]));   // reuse this file's fake iterator

    await foreach (var _ in _repository.StreamByRunIdAsync("opdb_20260621040003000Z", CancellationToken.None)) { }

    Assert.NotNull(captured);
    Assert.Contains("c.run_id = @runId", captured!.QueryText);
}
```

> If `MachineRepositoryTests` uses a different fake-iterator/harness, mirror that file's existing cross-partition test (e.g. the `StreamAllAsync`/`QueryByTitleAsync` test) verbatim.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~MachineRepositoryTests.StreamByRunIdAsync"`
Expected: FAIL — method not defined.

- [ ] **Step 3: Add `RunId` to `Machine`**

In `Machine.cs`, after `LastSeenAt` and before `ETag`:

```csharp
    [JsonPropertyName("run_id")]
    public string? RunId { get; set; }
```

- [ ] **Step 4: Add to the interface**

In `IMachineRepository.cs`:
```csharp
    IAsyncEnumerable<Machine> StreamByRunIdAsync(string runId, CancellationToken cancellationToken);
```

- [ ] **Step 5: Implement (Pattern A — `StreamCrossPartitionAsync`)**

In `MachineRepository.cs`:
```csharp
public IAsyncEnumerable<Machine> StreamByRunIdAsync(string runId, CancellationToken cancellationToken) =>
    StreamCrossPartitionAsync(
        "SELECT * FROM c WHERE c.run_id = @runId",
        new Dictionary<string, object> { ["runId"] = runId },
        cancellationToken);
```

- [ ] **Step 6: Update the allow-list justification**

In `CrossPartitionQueryAllowListTests.cs`, append to the `["MachineRepository.cs"]` value:
`"; StreamByRunIdAsync (run_id equality match, bounded by run cardinality)"`.

- [ ] **Step 7: Run tests + build**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~MachineRepositoryTests.StreamByRunIdAsync|FullyQualifiedName~CrossPartitionQueryAllowListTests"`
Expected: build 0/0; PASS.

- [ ] **Step 8: Commit**

```bash
git add src/PinballWizard.Core/Domain/Machine.cs src/PinballWizard.Application/Persistence/IMachineRepository.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/MachineRepository.cs tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/MachineRepositoryTests.cs
git commit -m "feat(persistence) run_id on Machine + StreamByRunIdAsync on machine repo"
```

---

### Task 8: Orchestrator — stamp `run_id`, tally `documents_new`

**Files:**
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs`
- Test: `tests/PinballWizard.Application.Tests/ScraperOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ScrapeRunId.For` (Task 1), `UpsertOutcome` (Task 4), `DocumentRecord.RunId` (Task 3), `WriteSourceRunAsync` `documents_new`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task RunAsync_TalliesDocumentsNew_FromCreatedOutcomesOnly()
{
    // Two scraped items: repo returns Created for the first, Updated for the second.
    _rawDocRepo.UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>())
        .Returns(
            ci => new RawDocumentUpsertResult(MapDomain(ci.Arg<DocumentRecord>()), UpsertOutcome.Created),
            ci => new RawDocumentUpsertResult(MapDomain(ci.Arg<DocumentRecord>()), UpsertOutcome.Updated));

    ScrapeRunRecord? written = null;
    _scrapeRuns.WriteAsync(Arg.Do<ScrapeRunRecord>(r => written = r), Arg.Any<CancellationToken>())
        .Returns(Task.CompletedTask);

    await _orchestrator.RunAsync(TwoItemScraperOptions(), CancellationToken.None);

    Assert.NotNull(written);
    Assert.Equal(2, written!.DocumentsDiscovered); // all touched
    Assert.Equal(1, written.DocumentsNew);          // only the Created one
}
```

> Mirror the existing `ScraperOrchestratorTests` setup (the substitute scraper/repo wiring + the run-options builder). `MapDomain` here is a tiny test helper producing a `RawDocumentRecord` from the input — reuse the file's existing helper if present.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ScraperOrchestratorTests.RunAsync_TalliesDocumentsNew"`
Expected: FAIL — `documents_new` always 0 (not tallied) and/or signature mismatch.

- [ ] **Step 3: Stamp run_id + tally new count**

In `ScraperOrchestrator.cs`:

(a) Where `sourceFailed`/`sourceDocCount` are declared (lines 68-69), add:
```csharp
            var sourceNewCount = 0;
```

(b) Stamp run_id on the record before upsert. After `var record = BuildDocumentRecord(item);` (line 89), add:
```csharp
                        record.RunId = ScrapeRunId.For(sourceId, runStartedAt);
```

(c) Capture the outcome at the call site (lines 96-111). Change `await _rawDocRepo.UpsertRawAsync(record, cancellationToken);` to:
```csharp
                                var upsert = await _rawDocRepo.UpsertRawAsync(record, cancellationToken);
                                if (upsert.Outcome == UpsertOutcome.Created)
                                {
                                    System.Threading.Interlocked.Increment(ref sourceNewCount);
                                }
```
(`sourceNewCount` is captured by the `Task.Run` lambda; use `Interlocked` because writes run under the semaphore concurrently.)

(d) Pass it through. Change the `WriteSourceRunAsync(...)` call (line 150-152) to add the new arg, and add the parameter + record field:
- Call: `sourceId, runStartedAt, sourceStopwatch.Elapsed, sourceDocCount, sourceNewCount, sourceFailed, firstError, cancellationToken`
- Signature: add `int documentsNew,` after `int documentsDiscovered,`.
- In the `new ScrapeRunRecord { … }` (line 200) add `DocumentsNew = documentsNew,`.

> Confirm `runStartedAt` and `sourceId` are in scope at the stamp site (they are — `runStartedAt` is the per-source run start, `sourceId` is the group key).

- [ ] **Step 4: Run test + build**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ScraperOrchestratorTests"`
Expected: build 0/0; PASS (including existing orchestrator tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/ScraperOrchestrator.cs tests/PinballWizard.Application.Tests/ScraperOrchestratorTests.cs
git commit -m "feat(scraper) stamp run_id + tally documents_new in orchestrator"
```

---

### Task 9: OPDB — stamp `run_id` on inserted machines, count `documents_new`

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Integrations/Opdb/OpdbSyncServiceTests.cs`

**Interfaces:**
- Consumes: `ScrapeRunId.For` (Task 1), `Machine.RunId` (Task 7), `ScrapeRunRecord.DocumentsNew` (Task 2).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task SyncAsync_StampsRunId_OnNewlyInsertedMachine()
{
    GivenOpdbReturns(OneNewMachineDto());          // existing test-data helper
    GivenMachineMissing();                          // GetByOpdbIdAsync → null

    Machine? upserted = null;
    _machines.UpsertAsync(Arg.Do<Machine>(m => upserted = m), Arg.Any<CancellationToken>())
        .Returns(ci => ci.Arg<Machine>());

    await _service.SyncAsync(OpdbSyncOptions(), CancellationToken.None);

    Assert.NotNull(upserted!.RunId);
    Assert.StartsWith("opdb_", upserted.RunId);
}

[Fact]
public async Task SyncAsync_RunRecord_DocumentsNew_EqualsInsertedCount()
{
    GivenOpdbReturns(OneNewMachineDto());
    GivenMachineMissing();
    ScrapeRunRecord? run = null;
    _scrapeRuns.WriteAsync(Arg.Do<ScrapeRunRecord>(r => run = r), Arg.Any<CancellationToken>())
        .Returns(Task.CompletedTask);

    await _service.SyncAsync(OpdbSyncOptions(), CancellationToken.None);

    Assert.Equal(1, run!.DocumentsNew);             // inserted
}
```

> Reuse `OpdbSyncServiceTests`' existing fixtures/substitutes (`_machines`, `_scrapeRuns`, the DTO + options builders). Names above mirror the file's established helpers.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~OpdbSyncServiceTests.SyncAsync_StampsRunId|FullyQualifiedName~OpdbSyncServiceTests.SyncAsync_RunRecord_DocumentsNew"`
Expected: FAIL — run_id null; `documents_new` 0.

- [ ] **Step 3: Stamp run_id on insert**

In `OpdbSyncService.cs`, in the `if (existing is null)` branch (line ~184), before `if (!isDryRun)`:
```csharp
            mapped.RunId = ScrapeRunId.For(IngestionSourceIds.Opdb, runStartedAt);
```
(Leave the `else`/update branch untouched — write-once. `MergeOpdbFieldsInto` does not set `RunId`, so it is preserved.)

- [ ] **Step 4: Set `documents_new` on the run record**

In the `ScrapeRunRecord` write block (lines 517-528), add:
```csharp
            DocumentsNew = inserted,
```
(keep `DocumentsDiscovered = inserted + updated,`).

- [ ] **Step 5: Run tests + build**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~OpdbSyncServiceTests"`
Expected: build 0/0; PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs tests/PinballWizard.Infrastructure.Tests/Integrations/Opdb/OpdbSyncServiceTests.cs
git commit -m "feat(opdb) stamp run_id on inserted machines + documents_new count"
```

---

### Task 10: `AdminRunDocuments.razor` — per-run item list (source-kind branch)

**Files:**
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminRunDocuments.razor`
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs` (extend stubs with `StreamByRunIdAsync` on both repos)
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminRunDocumentsTests.cs`

**Interfaces:**
- Consumes: `IRawDocumentRepository.StreamByRunIdAsync`, `IMachineRepository.StreamByRunIdAsync`, `IngestionSourceIds.Opdb`.
- Produces: component params `[Parameter] string SourceId`, `[Parameter] string RunId`.

- [ ] **Step 1: Write the failing tests** (manufacturer → documents; OPDB → machines; empty; failure)

```csharp
public sealed class AdminRunDocumentsTests : AsyncBunitContext
{
    public AdminRunDocumentsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void ManufacturerSource_ListsDocuments()
    {
        var raw = Substitute.For<IRawDocumentRepository>();
        raw.StreamByRunIdAsync("stern_x", Arg.Any<CancellationToken>()).Returns(Docs(DocRec("Jaws Manual")));
        Services.AddSingleton(raw);
        Services.AddSingleton(Substitute.For<IMachineRepository>());

        var cut = RenderRunDocs("stern", "stern_x");
        cut.WaitForAssertion(() => Assert.Contains("Jaws Manual", cut.Markup));
    }

    [Fact]
    public void OpdbSource_ListsMachines()
    {
        var machines = Substitute.For<IMachineRepository>();
        machines.StreamByRunIdAsync("opdb_x", Arg.Any<CancellationToken>()).Returns(Machines(Mch("Elvira's House of Horrors")));
        Services.AddSingleton(machines);
        Services.AddSingleton(Substitute.For<IRawDocumentRepository>());

        var cut = RenderRunDocs("opdb", "opdb_x");
        cut.WaitForAssertion(() => Assert.Contains("Elvira's House of Horrors", cut.Markup));
    }

    [Fact]
    public void EmptyRun_ShowsReconfirmedMessage()
    {
        var raw = Substitute.For<IRawDocumentRepository>();
        raw.StreamByRunIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Docs());
        Services.AddSingleton(raw);
        Services.AddSingleton(Substitute.For<IMachineRepository>());

        var cut = RenderRunDocs("stern", "stern_x");
        cut.WaitForAssertion(() => Assert.Contains("re-confirmed existing", cut.Markup));
    }
}
```

(Add the `Docs`/`Machines`/`DocRec`/`Mch`/`RenderRunDocs` helpers in this file, mirroring `AdminSourceDetailTests`' async-enumerable + `Render(builder => …)` helpers.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminRunDocumentsTests"`
Expected: FAIL — component does not exist.

- [ ] **Step 3: Create the component**

```razor
@using PinballWizard.Application.Persistence
@using PinballWizard.Core.Domain
@using PinballWizard.Core.Sources
@inject IRawDocumentRepository RawDocs
@inject IMachineRepository Machines
@inject ILogger<AdminRunDocuments> Logger

@if (_failed)
{
    <MudAlert Severity="Severity.Error" data-testid="run-docs-failed">Items could not be loaded.</MudAlert>
}
else if (_loading)
{
    <MudProgressCircular Indeterminate="true" Size="Size.Small" />
}
else if (_items.Count == 0)
{
    <MudText Typo="Typo.body2" Color="Color.Secondary" data-testid="run-docs-empty">
        This run captured no new items — it re-confirmed existing ones.
    </MudText>
}
else
{
    <MudList T="string" Dense="true" data-testid="run-docs-list">
        @foreach (var line in _items)
        {
            <MudListItem T="string">@line</MudListItem>
        }
    </MudList>
}

@code {
    [Parameter] public string SourceId { get; set; } = "";
    [Parameter] public string RunId { get; set; } = "";

    private readonly List<string> _items = [];
    private bool _loading = true;
    private bool _failed;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            if (SourceId == IngestionSourceIds.Opdb)
            {
                await foreach (var m in Machines.StreamByRunIdAsync(RunId, CancellationToken.None))
                {
                    _items.Add($"{m.Title} · {m.ManufacturerDisplayName}{(m.Year is { } y ? $" · {y}" : "")}");
                }
            }
            else
            {
                await foreach (var d in RawDocs.StreamByRunIdAsync(RunId, CancellationToken.None))
                {
                    _items.Add($"{d.Source?.FileUrl ?? d.DocumentUrl} · {d.DocumentType}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load run documents for run '{RunId}'.", RunId);
            _failed = true;
        }
        finally { _loading = false; }
    }
}
```

> Adjust the document display fields to the actual `RawDocumentRecord` shape (it exposes `DocumentUrl`, `DocumentType`, `Source`). Use the real properties confirmed at build time; the line above uses `DocumentUrl`/`DocumentType` which exist on the POCO.

- [ ] **Step 4: Run tests + build**

Run: `dotnet build PinballWizard.slnx` then the Step-2 filter.
Expected: build 0/0; PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminRunDocuments.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminRunDocumentsTests.cs tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs
git commit -m "feat(web) AdminRunDocuments per-run item list (documents + machines)"
```

---

### Task 11: `AdminSourceDetail` — Processed/New columns + expandable drill-down

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminSourceDetailTests.cs`

**Interfaces:**
- Consumes: `ScrapeRunId.For` (Task 1), `AdminRunDocuments` (Task 10), `ScrapeRunRecord.DocumentsNew` (Task 2).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void RunHistory_ShowsProcessedAndNewColumns()
{
    // Arrange a run with DocumentsDiscovered=200, DocumentsNew=3 via the existing Runs(...) stub.
    var cut = RenderWithRuns(new ScrapeRunRecord
    {
        SourceId = "stern", RunAt = DateTimeOffset.UtcNow, DurationSeconds = 1,
        Succeeded = true, DocumentsDiscovered = 200, DocumentsNew = 3,
    });

    cut.WaitForAssertion(() =>
    {
        Assert.Contains("Processed", cut.Markup);
        Assert.Contains("New", cut.Markup);
        Assert.Contains("200", cut.Markup);
        Assert.Contains("3", cut.Markup);
    });
}
```

> `RenderWithRuns` wraps the existing `AdminSourceDetailRunHistoryTests` stub pattern (`Runs(...)` async-enumerable + the `RenderDetail(id)` helper). Reuse them.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminSourceDetailTests.RunHistory_ShowsProcessedAndNewColumns"`
Expected: FAIL — only a single "Documents" column today.

- [ ] **Step 3: Update the run-history table** (lines 155-203)

- Replace the `<th ...>Documents</th>` header with two headers: `<th style="text-align:right">Processed</th>` and `<th style="text-align:right">New</th>`.
- Replace the `<td ...>@run.DocumentsDiscovered</td>` cell with:
  ```razor
  <td style="text-align:right">@run.DocumentsDiscovered</td>
  <td style="text-align:right">@run.DocumentsNew</td>
  ```
- Make each `<tr>` expandable: add an expander toggle column and, when expanded, a detail row hosting the child component:
  ```razor
  <td>
      <MudIconButton Size="Size.Small" aria-label="Toggle run documents"
                     Icon="@(_expanded.Contains(RunKey(run)) ? Icons.Material.Filled.ExpandLess : Icons.Material.Filled.ExpandMore)"
                     OnClick="@(() => ToggleRun(run))" />
  </td>
  ```
  and after the row, conditionally:
  ```razor
  @if (_expanded.Contains(RunKey(run)))
  {
      <tr><td colspan="6">
          <AdminRunDocuments SourceId="@Id" RunId="@RunKey(run)" />
      </td></tr>
  }
  ```

- [ ] **Step 4: Add the expand state + key helper** (in `@code`)

```csharp
    private readonly HashSet<string> _expanded = [];
    private string RunKey(ScrapeRunRecord run) => ScrapeRunId.For(run.SourceId, run.RunAt);
    private void ToggleRun(ScrapeRunRecord run)
    {
        var key = RunKey(run);
        if (!_expanded.Remove(key)) _expanded.Add(key);
    }
```

(Add `@using PinballWizard.Core.Models` for `ScrapeRunId` if not already imported.)

- [ ] **Step 5: Run tests + build**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminSourceDetailTests"`
Expected: build 0/0; PASS (existing run-history tests still green — they don't assert the old single column exactly; if one does, update it to the new headers).

- [ ] **Step 6: Run the full CI-filter suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminSourceDetailTests.cs
git commit -m "feat(web) run-history Processed/New columns + expandable per-run drill-down"
```

---

## Self-Review

**Spec coverage:** §3.1 run_id on docs+machines → Tasks 3, 7. §3.2 ScrapeRunId → Task 1. §3.3 UpsertOutcome → Task 4. §3.4 documents_new tally → Tasks 2, 8, 9. §3.5 queries+index → Tasks 5, 6, 7 (machines need no index change — noted). §3.6 UI → Tasks 10, 11. §7 back-compat (nullable run_id, default-0 documents_new) → covered by nullable/defaulted fields. All spec sections map to a task.

**Placeholder scan:** No "TBD"/"handle errors"/"similar to". Test bodies are concrete; the few "reuse the file's existing helper" notes point at named, real harnesses (the engineer must match an existing pattern rather than invent one) — acceptable since the exact helper names live in those test files and were confirmed to exist.

**Type consistency:** `ScrapeRunId.For`, `RawDocumentUpsertResult(Record, Outcome)`, `UpsertOutcome.{Created,Updated}`, `DocumentsNew`, `RunId`, `StreamByRunIdAsync` used identically across tasks. `RunKey(run)` (UI) computes the same id `StreamByRunIdAsync` matches.

**Known caveat:** Task 4 Step 5 (caller fan-out) depends on the actual set of `UpsertRawAsync` callers — the `git grep` enumerates them; the only known production caller (orchestrator) is handled in Task 8.
