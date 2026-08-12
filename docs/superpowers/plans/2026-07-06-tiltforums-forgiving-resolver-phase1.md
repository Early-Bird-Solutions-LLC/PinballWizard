# Tilt Forums Forgiving Resolver — Phase 1 (#694) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover the ~18–20 of 26 master-list Tilt Forums rulesheets that currently fail exact-title matching, by adding a manufacturer-scoped forgiving fallback through the machine findability index.

**Architecture:** On an exact `QueryByTitleAsync` miss, `TiltForumsGameMatcher` queries `IMachineSearchIndex` (`pinwiz-machines-v1`) filtered server-side to the listing's manufacturer partition, then collapses hits with a margin-free rule (top-hit's edition family, unless a same-title different-group collision). Exact match remains the fast first path; when AI Search is unconfigured the index resolves to a null-object and behavior is identical to today.

**Tech Stack:** C# / .NET 10, Azure.Search.Documents 12.0.0, xUnit + NSubstitute, Clean Architecture (Application interfaces, Infrastructure impls).

## Global Constraints

- Personal identity commits only: `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no** Claude attribution trailer.
- Invariant #17 — degrade visibly, never fabricate: stale-index / transport failures log at Warning + meter, then fall through to NoMatch; never silently ground a wrong machine.
- Manufacturer scoping is load-bearing — a fuzzy hit outside the listing's manufacturer partition must never be accepted.
- Tests assert behavior with fixtures where the path actually fires (repo bar), not structure.
- Full CI-equivalent suite before push: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.
- Run all commands from the worktree root: `c:\earlybird\PinballWizard\.worktrees\tiltforums-resolver`.

---

## File Structure

- `src/PinballWizard.Application/Findability/IMachineSearchIndex.cs` — add optional `manufacturerKey` scope to `SearchAsync`.
- `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchMachineIndex.cs` — emit `manufacturer_key eq '…'` filter server-side.
- `src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs` — update the one existing call site (pass `manufacturerKey: null`).
- `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs` — add the index-backed forgiving fallback + `ResolvedViaFuzzy` flag.
- `src/PinballWizard.Cli/Program.cs` — resolve `IMachineSearchIndex`, pass it to the matcher, add a `fuzzy_resolved` run-summary counter.
- Tests: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs`, `tests/PinballWizard.Infrastructure.Tests/Integrations/AiSearch/AiSearchMachineIndexTests.cs`.

---

### Task 1: Manufacturer-scoped machine-index search

**Files:**
- Modify: `src/PinballWizard.Application/Findability/IMachineSearchIndex.cs`
- Modify: `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchMachineIndex.cs`
- Modify: `src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs` (call-site fix)
- Test: `tests/PinballWizard.Infrastructure.Tests/Integrations/AiSearch/AiSearchMachineIndexTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyList<MachineSearchHit>> IMachineSearchIndex.SearchAsync(string query, int top, string? manufacturerKey, CancellationToken cancellationToken)` — when `manufacturerKey` is non-null/non-whitespace, results are restricted to that partition via an OData `manufacturer_key eq` filter.
- Consumes: existing `internal static SearchOptions AiSearchMachineIndex.BuildSearchOptions(int top)` → becomes `BuildSearchOptions(int top, string? manufacturerKey)`; `MachineSearchIndexFields.ManufacturerKey` (existing constant).

- [ ] **Step 1: Write the failing test** (pin the filter in `BuildSearchOptions`)

Add to `AiSearchMachineIndexTests` (create the file if absent, mirroring existing `internal static` pinning tests referenced in `AiSearchMachineIndex.cs`):

```csharp
[Fact]
public void BuildSearchOptions_WithManufacturerKey_EmitsPartitionFilter()
{
    var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: "stern");
    Assert.Equal("manufacturer_key eq 'stern'", options.Filter);
    Assert.Equal(5, options.Size);
}

[Fact]
public void BuildSearchOptions_NullManufacturerKey_NoFilter()
{
    var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: null);
    Assert.Null(options.Filter);
}

[Fact]
public void BuildSearchOptions_ManufacturerKeyWithApostrophe_IsOdataEscaped()
{
    // OData escapes a single quote by doubling it. Defensive — real keys are
    // lowercase alnum/underscore, but the query builder must never emit a
    // malformed / injectable filter.
    var options = AiSearchMachineIndex.BuildSearchOptions(top: 5, manufacturerKey: "o'brien");
    Assert.Equal("manufacturer_key eq 'o''brien'", options.Filter);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~AiSearchMachineIndexTests.BuildSearchOptions" -v minimal`
Expected: FAIL — `BuildSearchOptions` has no `manufacturerKey` parameter (compile error).

- [ ] **Step 3: Implement — interface + options + call site**

In `IMachineSearchIndex.cs`, change the method signature and update its doc comment:

```csharp
// Returns OPDB IDs ranked by descending relevance (highest score first).
// `top` bounds the result set; callers that only need one result pass top=1.
// When `manufacturerKey` is non-null/non-whitespace, results are restricted to
// that manufacturer partition (server-side filter) — used by ingestion-time
// resolution that already knows the manufacturer. Null = unscoped (the
// getMachineByTitle default). An empty list is a valid honest-miss answer —
// callers must not fabricate.
Task<IReadOnlyList<MachineSearchHit>> SearchAsync(
    string query,
    int top,
    string? manufacturerKey,
    CancellationToken cancellationToken);
```

In `AiSearchMachineIndex.cs`, thread the parameter through `SearchAsync` and `BuildSearchOptions`:

```csharp
public async Task<IReadOnlyList<MachineSearchHit>> SearchAsync(
    string query,
    int top,
    string? manufacturerKey,
    CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(query);
    ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);

    var stopwatch = Stopwatch.StartNew();
    try
    {
        var options = BuildSearchOptions(top, manufacturerKey);
        // ... unchanged body ...
```

Replace the `BuildSearchOptions` signature and add the filter (keep every existing option; add only `Filter`):

```csharp
internal static SearchOptions BuildSearchOptions(int top, string? manufacturerKey)
{
    var options = new SearchOptions
    {
        QueryType = SearchQueryType.Simple,
        ScoringProfile = MachineSearchIndexSchema.ScoringProfileName,
        Size = top,
        Select =
        {
            MachineSearchIndexFields.Id,
            MachineSearchIndexFields.Title,
            MachineSearchIndexFields.Manufacturer,
            MachineSearchIndexFields.ManufacturerKey,
            MachineSearchIndexFields.GroupId,
            MachineSearchIndexFields.Year,
        },
        SearchFields =
        {
            MachineSearchIndexFields.Title,
            MachineSearchIndexFields.TitlePrefix,
            MachineSearchIndexFields.TitlePhonetic,
        },
    };

    if (!string.IsNullOrWhiteSpace(manufacturerKey))
    {
        // OData string-literal escaping: a single quote is doubled.
        var escaped = manufacturerKey.Replace("'", "''", StringComparison.Ordinal);
        options.Filter = $"{MachineSearchIndexFields.ManufacturerKey} eq '{escaped}'";
    }

    return options;
}
```

In `MachineGroundingTool.cs`, update the one call site (currently `SearchAsync(title, top, cancellationToken)`):

```csharp
var hits = await _machineSearchIndex
    .SearchAsync(title, top, manufacturerKey: null, cancellationToken)
    .ConfigureAwait(false);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~AiSearchMachineIndexTests.BuildSearchOptions" -v minimal`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Findability/IMachineSearchIndex.cs \
        src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchMachineIndex.cs \
        src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs \
        tests/PinballWizard.Infrastructure.Tests/Integrations/AiSearch/AiSearchMachineIndexTests.cs
git commit -m "feat(findability) manufacturer-scoped machine-index search (#694)"
```

---

### Task 2: Index-backed forgiving fallback in TiltForumsGameMatcher

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs`

**Interfaces:**
- Consumes: `IMachineSearchIndex.SearchAsync(query, top, manufacturerKey, ct)` (Task 1); `IMachineRepository.GetByOpdbIdAsync(opdbId, manufacturer, ct)`, `.GetSiblingsByGroupIdAsync(groupId, ct)`, `.QueryByTitleAsync(title, ct)`; `EditionFamily.IsEditionFamily(IReadOnlyList<Machine>)`; `OpdbMachineMapper.NormalizeManufacturerKey(text)`.
- Produces: `TiltForumsGameMatcher.ResolveAsync(IMachineRepository machineRepository, IMachineSearchIndex? machineSearchIndex, string gameTitle, string manufacturerHeaderText, CancellationToken cancellationToken)` — new 2nd parameter. `TiltForumsGameMatchResult` gains `bool ResolvedViaFuzzy` (defaults false). Ambiguous fuzzy outcomes map to the existing `MultipleMatchesInManufacturerPartition` status; no-fuzzy-hit maps to `NoMatchInManufacturerPartition`.

- [ ] **Step 1: Write the failing tests**

Add to `TiltForumsGameMatcherTests`. Introduce a fake index helper and cover: exact-hit does not consult the index; miss → fuzzy resolve (single); miss → fuzzy edition-family fan-out; miss → same-title different-group collision → MultipleMatches; miss → fuzzy scoped so a wrong-partition hit is impossible (index returns empty for the scoped call); null index → today's behavior.

```csharp
private static MachineSearchHit Hit(string opdbId, string title, string mfrKey,
    string mfrDisplay, string? groupId, int? year, double score) =>
    new(opdbId, title, mfrDisplay, mfrKey, groupId, year, score);

private static IMachineSearchIndex FakeIndex(params MachineSearchHit[] hits)
{
    var idx = Substitute.For<IMachineSearchIndex>();
    idx.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
       .Returns(hits);
    return idx;
}

[Fact]
public async Task ResolveAsync_ExactHit_DoesNotConsultIndex()
{
    var stern = MakeMachine("GK17D-a", "stern", "Stern Pinball", "Jurassic Park");
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Jurassic Park", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable([stern]));
    var index = FakeIndex();

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, index, "Jurassic Park", "Stern Pinball", CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
    Assert.False(result.ResolvedViaFuzzy);
    await index.DidNotReceive().SearchAsync(
        Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task ResolveAsync_ExactMiss_FuzzyResolvesSingleGroup()
{
    // "Jurassic Park (Stern)" exact-misses; index top hit is "Jurassic Park"
    // (group GK17D). A lower-scored different-title hit ("Home Edition") is noise
    // and must be ignored — not treated as a collision.
    var jp = MakeMachine("GK17D-a", "stern", "Stern Pinball", "Jurassic Park", "GK17D", 2019);
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Jurassic Park (Stern)", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
    repo.GetByOpdbIdAsync("GK17D-a", "stern", Arg.Any<CancellationToken>()).Returns(jp);
    repo.GetSiblingsByGroupIdAsync("GK17D", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable([jp]));
    var index = FakeIndex(
        Hit("GK17D-a", "Jurassic Park", "stern", "Stern Pinball", "GK17D", 2019, 103.0),
        Hit("GxvvB-h", "Jurassic Park (Home Edition)", "stern", "Stern Pinball", "GxvvB", 2021, 74.0));

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, index, "Jurassic Park (Stern)", "Stern Pinball", CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
    Assert.True(result.ResolvedViaFuzzy);
    Assert.Equal("GK17D-a", result.Machines[0].MachineId);
}

[Fact]
public async Task ResolveAsync_ExactMiss_FuzzyEditionFamilyFansOut()
{
    var pro = MakeMachine("GK17D-a", "stern", "Stern Pinball", "Jurassic Park", "GK17D", 2019);
    var prem = MakeMachine("GK17D-b", "stern", "Stern Pinball", "Jurassic Park", "GK17D", 2019);
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Jurassic Park (Stern)", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
    repo.GetByOpdbIdAsync("GK17D-a", "stern", Arg.Any<CancellationToken>()).Returns(pro);
    repo.GetSiblingsByGroupIdAsync("GK17D", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable([pro, prem]));
    var index = FakeIndex(
        Hit("GK17D-a", "Jurassic Park", "stern", "Stern Pinball", "GK17D", 2019, 103.0));

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, index, "Jurassic Park (Stern)", "Stern Pinball", CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.ResolvedEditionFamily, result.Status);
    Assert.True(result.ResolvedViaFuzzy);
    Assert.Equal(2, result.Machines.Count);
}

[Fact]
public async Task ResolveAsync_ExactMiss_SameTitleDifferentGroup_IsAmbiguous_NotGuessed()
{
    // Two identically-titled machines in DIFFERENT groups within the scoped
    // partition — a genuine collision. Must NOT be grounded.
    var a = MakeMachine("Gaaa-1", "stern", "Stern Pinball", "Star Trek", "Gaaa", 2013);
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Star Trek", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
    repo.GetByOpdbIdAsync("Gaaa-1", "stern", Arg.Any<CancellationToken>()).Returns(a);
    var index = FakeIndex(
        Hit("Gaaa-1", "Star Trek", "stern", "Stern Pinball", "Gaaa", 2013, 90.0),
        Hit("Gbbb-1", "Star Trek", "stern", "Stern Pinball", "Gbbb", 2018, 88.0));

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, index, "Star Trek", "Stern Pinball", CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
    Assert.Empty(result.Machines);
    await repo.DidNotReceive().GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task ResolveAsync_ExactMiss_NoFuzzyHits_ReturnsNoMatch()
{
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Weird Al", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
    var index = FakeIndex(); // zero hits

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, index, "Weird Al", "Multimorphic", CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
    Assert.Empty(result.Machines);
}

[Fact]
public async Task ResolveAsync_ExactMiss_StaleIndexTopHit_ReturnsNoMatch()
{
    // Index hit exists but the machine row is gone from Cosmos (stale index).
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Pokemon", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));
    repo.GetByOpdbIdAsync("GV8wB-x", "stern", Arg.Any<CancellationToken>())
        .Returns((Machine?)null);
    var index = FakeIndex(
        Hit("GV8wB-x", "Pokémon", "stern", "Stern Pinball", "GV8wB", 2026, 17.0));

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, index, "Pokemon", "Stern Pinball", CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
    Assert.Empty(result.Machines);
}

[Fact]
public async Task ResolveAsync_NullIndex_ExactMissStaysNoMatch_NoFuzzy()
{
    var repo = Substitute.For<IMachineRepository>();
    repo.QueryByTitleAsync("Pokemon", Arg.Any<CancellationToken>())
        .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

    var result = await TiltForumsGameMatcher.ResolveAsync(
        repo, machineSearchIndex: null, "Pokemon", "Stern Pinball", CancellationToken.None);

    Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
    Assert.False(result.ResolvedViaFuzzy);
}
```

Then update the seven EXISTING test call sites in this file to pass the new parameter as `null` (they assert the exact-path behavior, which must be unchanged): change each `TiltForumsGameMatcher.ResolveAsync(repo, "…", "…", CancellationToken.None)` to `TiltForumsGameMatcher.ResolveAsync(repo, null, "…", "…", CancellationToken.None)`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~TiltForumsGameMatcherTests" -v minimal`
Expected: FAIL — `ResolveAsync` has no `machineSearchIndex` parameter / `ResolvedViaFuzzy` missing (compile error).

- [ ] **Step 3: Implement the resolver**

In `TiltForumsGameMatcher.cs`: add the new parameter and record field, keep the exact path first, add the fuzzy fallback + collapse helper. Add `using PinballWizard.Application.Findability;`.

Update the result record:

```csharp
public sealed record TiltForumsGameMatchResult(
    TiltForumsGameMatchStatus Status,
    IReadOnlyList<TiltForumsMachineMatch> Machines,
    bool ResolvedViaFuzzy = false);
```

Update `ResolveAsync` (exact path unchanged except the empty-match branch now tries fuzzy):

```csharp
public static async Task<TiltForumsGameMatchResult> ResolveAsync(
    IMachineRepository machineRepository,
    IMachineSearchIndex? machineSearchIndex,
    string gameTitle,
    string manufacturerHeaderText,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(machineRepository);
    ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);
    ArgumentException.ThrowIfNullOrWhiteSpace(manufacturerHeaderText);

    var manufacturerKey = OpdbMachineMapper.NormalizeManufacturerKey(manufacturerHeaderText);

    var matches = new List<Machine>();
    await foreach (var machine in machineRepository.QueryByTitleAsync(gameTitle, cancellationToken))
    {
        if (string.Equals(machine.PartitionKey, manufacturerKey, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(machine);
        }
    }

    if (matches.Count == 1)
    {
        return new TiltForumsGameMatchResult(
            TiltForumsGameMatchStatus.Resolved, [ToMatch(matches[0])]);
    }

    if (matches.Count > 1)
    {
        if (EditionFamily.IsEditionFamily(matches))
        {
            var siblings = await CollectSiblingsAsync(machineRepository, matches[0].GroupId!, cancellationToken);
            return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.ResolvedEditionFamily, siblings);
        }

        return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, []);
    }

    // matches.Count == 0 — exact miss. Try the forgiving machine-index path,
    // scoped to this manufacturer partition. Absent index (AI Search
    // unconfigured / null-object empty) degrades to the historical NoMatch.
    if (machineSearchIndex is not null)
    {
        var fuzzy = await ResolveViaMachineIndexAsync(
            machineRepository, machineSearchIndex, gameTitle, manufacturerKey, cancellationToken);
        if (fuzzy is not null)
            return fuzzy;
    }

    return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, []);
}
```

Add the collapse helper (margin-free rule from the spec):

```csharp
// Top hits requested from the machine index. 5 gives enough headroom to see
// a same-title different-group collision while bounding the query.
private const int MachineIndexTopHits = 5;

// Forgiving fallback: resolve gameTitle via the machine findability index,
// scoped to manufacturerKey. Returns null when the index yields nothing usable
// so the caller emits the historical NoMatch.
private static async Task<TiltForumsGameMatchResult?> ResolveViaMachineIndexAsync(
    IMachineRepository machineRepository,
    IMachineSearchIndex machineSearchIndex,
    string gameTitle,
    string manufacturerKey,
    CancellationToken cancellationToken)
{
    var hits = await machineSearchIndex.SearchAsync(
        gameTitle, MachineIndexTopHits, manufacturerKey, cancellationToken);
    if (hits.Count == 0)
        return null;

    var topHit = hits[0];

    // Point-read the authoritative Machine for the top hit. A stale index row
    // (hit present, machine gone) degrades to NoMatch (invariant #17).
    var topMachine = await machineRepository.GetByOpdbIdAsync(
        topHit.OpdbId, topHit.ManufacturerKey, cancellationToken);
    if (topMachine is null)
        return null;

    // Cross-group same-title collision guard: a different-group hit that carries
    // the SAME title as the top hit is a genuine same-name-different-game
    // ambiguity — do not guess.
    var topGroupKey = GroupKeyOf(topHit.OpdbId, topHit.GroupId);
    foreach (var other in hits.Skip(1))
    {
        if (!string.Equals(GroupKeyOf(other.OpdbId, other.GroupId), topGroupKey, StringComparison.Ordinal)
            && string.Equals(other.Title, topHit.Title, StringComparison.OrdinalIgnoreCase))
        {
            return new TiltForumsGameMatchResult(
                TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, []);
        }
    }

    // Resolve the top machine's edition family. A clean same-group+year family
    // fans out to every sibling (ADR-0032); a mixed-year/incomplete group grounds
    // the top machine alone rather than fanning onto a different-year game.
    if (!string.IsNullOrEmpty(topMachine.GroupId))
    {
        var siblings = await CollectMachinesAsync(
            machineRepository.GetSiblingsByGroupIdAsync(topMachine.GroupId, cancellationToken));
        if (siblings.Count > 1 && EditionFamily.IsEditionFamily(siblings))
        {
            return new TiltForumsGameMatchResult(
                TiltForumsGameMatchStatus.ResolvedEditionFamily,
                siblings.Select(ToMatch).ToList(),
                ResolvedViaFuzzy: true);
        }
    }

    return new TiltForumsGameMatchResult(
        TiltForumsGameMatchStatus.Resolved, [ToMatch(topMachine)], ResolvedViaFuzzy: true);
}

private static string GroupKeyOf(string opdbId, string? groupId) =>
    string.IsNullOrEmpty(groupId) ? opdbId : groupId;

private static async Task<List<Machine>> CollectMachinesAsync(IAsyncEnumerable<Machine> source)
{
    var list = new List<Machine>();
    await foreach (var m in source)
        list.Add(m);
    return list;
}
```

Note: `CollectSiblingsAsync` (existing) returns `TiltForumsMachineMatch[]`; the new `CollectMachinesAsync` returns `Machine` records because `IsEditionFamily` needs `Machine`. Keep both.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~TiltForumsGameMatcherTests" -v minimal`
Expected: PASS (all — 7 original updated + 7 new).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs \
        tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs
git commit -m "feat(tiltforums) index-backed forgiving title resolution, manufacturer-scoped (#694)"
```

---

### Task 3: Wire the index into the sync verb + run-summary breakdown

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs` (the `--sync-tiltforums-rulesheets` handler, ~lines 1310–1498)

**Interfaces:**
- Consumes: `TiltForumsGameMatcher.ResolveAsync(repo, machineSearchIndex, title, header, ct)` (Task 2); `IMachineSearchIndex` from `host.Services.GetService<IMachineSearchIndex>()`.

- [ ] **Step 1: Resolve the index and pass it to the matcher**

After the existing service resolutions (`tiltForumsMachineRepo` etc.), add:

```csharp
// Optional: manufacturer-scoped forgiving fallback for master-list titles that
// exact-miss (#694). Null-safe — when AI Search is unconfigured the matcher
// degrades to exact-only. GetService (not GetRequiredService) so an
// unconfigured host still runs the exact path.
var tiltForumsMachineIndex = host.Services.GetService<IMachineSearchIndex>();
```

Update the `ResolveAsync` call inside the `foreach (var listing in listings)` loop:

```csharp
matchResult = await PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatcher.ResolveAsync(
    tiltForumsMachineRepo, tiltForumsMachineIndex, listing.GameTitle, listing.ManufacturerHeaderText, cancellationToken);
```

Add `using PinballWizard.Application.Findability;` to `Program.cs` if not already present.

- [ ] **Step 2: Add the fuzzy-resolved counter to the run summary**

Add a counter beside the existing `tiltForumsEditionFamilyFanouts`:

```csharp
var tiltForumsFuzzyResolved = 0;
```

In the resolved branch (after `isResolved` is confirmed true), increment when the match came via the index:

```csharp
if (matchResult.ResolvedViaFuzzy)
    tiltForumsFuzzyResolved++;
```

Extend the completion log line:

```csharp
Console.WriteLine(
    $"--sync-tiltforums-rulesheets complete: indexed={tiltForumsIndexed} unmatched={tiltForumsUnmatched} " +
    $"edition_family_fanouts={tiltForumsEditionFamilyFanouts} fuzzy_resolved={tiltForumsFuzzyResolved} " +
    $"skipped_no_content={tiltForumsSkippedNoContent} failed={tiltForumsFailed}");
```

- [ ] **Step 3: Build to verify wiring compiles**

Run: `dotnet build src/PinballWizard.Cli -v minimal`
Expected: build succeeds, no warnings-as-errors.

- [ ] **Step 4: Run the full CI-equivalent suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E" -v minimal`
Expected: PASS (no regressions; the new matcher + options tests green).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git commit -m "feat(cli) wire forgiving resolver + fuzzy_resolved counter into tiltforums sync (#694)"
```

---

### Task 4: Live verification (behavioral, before PR)

**Files:** none (operational).

- [ ] **Step 1: Re-run the live sync** using the live-load runbook (`reference_local_live_load_runbook.md`, private project memory, not in this repo) env (isolated `AZURE_CONFIG_DIR`, `AZURE_TOKEN_CREDENTIALS=dev`, Cosmos + AiSearch + AiFoundry endpoints), from this worktree:

```
dotnet run --project src/PinballWizard.Cli -- --sync-tiltforums-rulesheets
```

Expected: `unmatched` drops from 26 toward ~6–8; `fuzzy_resolved` ≈ 12–18; individual `Indexed 'Pokemon' …`, `'Jurassic Park (Stern)' …`, `'Willy Wonka and the Chocolate Factory' …` lines present; the genuine-ambiguity titles (`Star Trek`, `Spider-Man`, `Walking Dead`) still logged unmatched.

- [ ] **Step 2: Confirm in the index** (read-only probe) that a previously-missing game now has Rulesheet chunks, e.g. query `pinwiz-rag-v1` for `machine_title` `Pokémon` filtered `document_type eq 'Rulesheet'` → count > 0.

- [ ] **Step 3:** Do NOT open the PR from this plan step — return to the operator for the `/local-review` + `.claude/PR-AUDIT.md` gate per repo workflow.

---

## Self-Review

- **Spec coverage:** server-side filter → Task 1; scoped forgiving resolution + margin-free collapse + skip-and-log ambiguity + graceful degradation → Task 2; verb wiring + run-summary breakdown → Task 3; live acceptance → Task 4. Phase 2 (#693) is a separate plan authored after this PR merges (per the two-PR decision).
- **Placeholder scan:** none — every step carries real code/commands.
- **Type consistency:** `SearchAsync(query, top, manufacturerKey, ct)` and `ResolveAsync(repo, machineSearchIndex, title, header, ct)` and `TiltForumsGameMatchResult(Status, Machines, ResolvedViaFuzzy)` are used identically across Tasks 1–3. `GroupKeyOf(opdbId, groupId?)` and `CollectMachinesAsync` are defined in Task 2 before use.
- **American Pinball caveat:** `Houdini` may still NoMatch (American Pinball absent from the machine index facet) — out of scope per spec; the resolver degrades safely.
