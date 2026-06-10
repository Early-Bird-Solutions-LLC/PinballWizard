# Option B — Pre-computed MatchTokens Disambiguation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store pre-computed user-typeable token sets per collision entry in `MachineTitleLookup` at OPDB sync time so `MachineGroundingTool` can correctly score all 10 manufacturer keys — including abbreviations like `"jjp"`, `"cgc"`, `"americanpinball"` — without runtime expansion.

**Architecture:** The `MachineTitleLookup` domain object gains a third parallel array `MatchTokens` (a `List<List<string>>`); each element is the expanded token set for the manufacturer key at the same index in `OpdbIds`/`Manufacturers`. `OpdbMachineMapper` gains a static lookup table (`ManufacturerMatchTokens`) mapping every known key to its token set. `OpdbSyncService.UpdateTitleLookupAsync` passes the tokens through `MachineTitleLookup.UpsertEntry`. `MachineGroundingTool.ScoreEntryAgainstTokens` switches from scoring the raw key to scoring against the stored token set. A one-off OPDB sync (or the next scheduled run) backfills existing rows.

**Tech Stack:** C# / .NET 10, Azure Cosmos DB (data-plane SDK), xUnit + NSubstitute, existing project test patterns.

---

## Background you must understand before touching code

### What problem does this solve?

`MachineGroundingTool.GetMachineByTitleAsync` scores collision entries to pick the right machine when multiple machines share a title (e.g., "Godzilla" → Sega 1998 or Stern 2021). The scorer does token overlap between user-input tokens and the stored manufacturer key. Keys like `"stern"` and `"sega"` are single words — a user typing "Stern Godzilla" produces the token `"stern"` which exactly matches the key. But keys like `"jjp"`, `"cgc"`, `"americanpinball"`, `"pinballbrothers"`, and `"barrelsoffun"` are abbreviations or concatenations — a user typing "Jersey Jack Pirates" never produces the token `"jjp"`, so the score stays 0 and the first-inserted entry wins (possibly the wrong one).

The fix: at write time (OPDB sync), expand each manufacturer key into all the tokens a user might type and store those alongside the entry. At query time, score the user tokens against the stored expanded tokens.

### Key file inventory

| File | Role |
|---|---|
| `src/PinballWizard.Core/Domain/MachineTitleLookup.cs` | Domain object — owns `OpdbIds`, `Manufacturers`, `UpsertEntry` |
| `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs` | Static mapper — contains `NormalizeManufacturerKey` switch (source of all stored keys) |
| `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs` | Calls `UpdateTitleLookupAsync` → `lookup.UpsertEntry` |
| `src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs` | Calls `ScoreEntryAgainstTokens` at query time |
| `tests/PinballWizard.Scraper.Tests/Ai/MachineGroundingToolTests.cs` | Unit tests for grounding tool |
| `tests/PinballWizard.Scraper.Tests/Integrations/Opdb/OpdbMachineMapperTests.cs` | Unit tests for the mapper |

### The three parallel arrays (post-change)

`MachineTitleLookup` currently has two index-aligned arrays per collision row:
- `OpdbIds[i]` — the OPDB machine ID
- `Manufacturers[i]` — the stored manufacturer key (e.g., `"jjp"`)

After this change there will be three:
- `OpdbIds[i]`
- `Manufacturers[i]`
- `MatchTokens[i]` — a list of lowercase tokens the user might type for this manufacturer (e.g., `["jjp", "jersey", "jack"]`)

`UpsertEntry` must keep all three in sync. The scorer uses `MatchTokens[i]` instead of `Manufacturers[i]`.

### Manufacturer key → MatchTokens expansion table

These are the 10 keys produced by `NormalizeManufacturerKey` in `OpdbMachineMapper.cs` and their expansions. Single-word keys already work (the token exactly equals the key); they get a one-element list for schema consistency:

| Stored key | MatchTokens |
|---|---|
| `"stern"` | `["stern"]` |
| `"jjp"` | `["jjp", "jersey", "jack"]` |
| `"americanpinball"` | `["americanpinball", "american", "pinball", "ap"]` |
| `"spooky"` | `["spooky"]` |
| `"multimorphic"` | `["multimorphic"]` |
| `"cgc"` | `["cgc", "chicago", "gaming"]` |
| `"haggis"` | `["haggis"]` |
| `"pinballbrothers"` | `["pinballbrothers", "pinball", "brothers", "pb"]` |
| `"dutch"` | `["dutch"]` |
| `"barrelsoffun"` | `["barrelsoffun", "barrels", "fun", "bof"]` |
| *(any fallback key)* | `[key]` — the key itself as a single-element list |

### Scoring change (MachineGroundingTool)

Current `ScoreEntryAgainstTokens` signature:
```csharp
internal static int ScoreEntryAgainstTokens(string manufacturerKey, IReadOnlyList<string> titleTokens)
```
Scores +1 per `titleToken` that equals `manufacturerKey`.

New signature:
```csharp
internal static int ScoreEntryAgainstTokens(IReadOnlyList<string> matchTokens, IReadOnlyList<string> titleTokens)
```
Scores +1 per `titleToken` that is contained in `matchTokens` (using `StringComparison.Ordinal`).

The call site in `GetMachineByTitleAsync` must supply `lookup.MatchTokens[i]`. For rows written before this deploy (no `MatchTokens` array yet), fall back to a single-element list containing the raw `Manufacturers[i]` key — preserving old behavior during the backfill window.

### Backward compatibility / backfill

Existing Cosmos rows written by the old sync have no `MatchTokens` field. `System.Text.Json` deserializes missing fields as `null` for reference types or the default value for value types. Since `MatchTokens` will be `List<List<string>>?` (nullable), a null value at query time means "no tokens stored yet — fall back to key-as-single-token." The next OPDB sync run backfills all rows automatically because `UpdateTitleLookupAsync` always RMW's the row.

---

## File changes summary

| File | Change |
|---|---|
| `src/PinballWizard.Core/Domain/MachineTitleLookup.cs` | Add `MatchTokens` property; extend `UpsertEntry` to accept and store `IReadOnlyList<string> matchTokens`; extend `RemoveEntry` to keep the third array in sync |
| `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs` | Add `ManufacturerMatchTokens` static dictionary; add `GetMatchTokens(string key)` static method |
| `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs` | Pass `matchTokens` through `UpdateTitleLookupAsync` → `lookup.UpsertEntry` |
| `src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs` | Change `ScoreEntryAgainstTokens` signature; update call site to pass `MatchTokens[i]` with fallback |
| `tests/PinballWizard.Scraper.Tests/Ai/MachineGroundingToolTests.cs` | Update existing tests; add new tests for multi-token scoring |
| `tests/PinballWizard.Scraper.Tests/Integrations/Opdb/OpdbMachineMapperTests.cs` | Add tests for `GetMatchTokens` |

---

## Task 1: Extend `MachineTitleLookup` with `MatchTokens`

**Files:**
- Modify: `src/PinballWizard.Core/Domain/MachineTitleLookup.cs`

- [ ] **Step 1: Write the failing tests for the extended `UpsertEntry` and `RemoveEntry`**

Open `tests/PinballWizard.Scraper.Tests/Ai/MachineGroundingToolTests.cs` — or if there is a dedicated `MachineTitleLookupTests.cs`, use that. Check with:

```powershell
Get-ChildItem tests\PinballWizard.Scraper.Tests -Recurse -Filter "*TitleLookup*"
```

If no dedicated file exists, add the tests to a new file:
`tests/PinballWizard.Scraper.Tests/Domain/MachineTitleLookupTests.cs`

```csharp
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Scraper.Tests.Domain;

public sealed class MachineTitleLookupTests
{
    [Fact]
    public void UpsertEntry_NewEntry_StoresMatchTokensAtSameIndex()
    {
        var lookup = new MachineTitleLookup { Id = "godzilla", PartitionKey = "godzilla" };
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);
        lookup.UpsertEntry("G5po2-MeP6B", "sega",  ["sega"]);

        Assert.Equal(2, lookup.OpdbIds.Count);
        Assert.Equal(2, lookup.MatchTokens!.Count);
        Assert.Equal(["stern"], lookup.MatchTokens[0]);
        Assert.Equal(["sega"],  lookup.MatchTokens[1]);
    }

    [Fact]
    public void UpsertEntry_ReplaceExisting_UpdatesMatchTokensInPlace()
    {
        var lookup = new MachineTitleLookup { Id = "godzilla", PartitionKey = "godzilla" };
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);
        // Re-upsert same opdbId with new tokens (simulates sync updating a row)
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern", "newtoken"]);

        Assert.Single(lookup.OpdbIds);
        Assert.Single(lookup.MatchTokens!);
        Assert.Equal(["stern", "newtoken"], lookup.MatchTokens[0]);
    }

    [Fact]
    public void RemoveEntry_ExistingEntry_RemovesMatchTokensAtSameIndex()
    {
        var lookup = new MachineTitleLookup { Id = "godzilla", PartitionKey = "godzilla" };
        lookup.UpsertEntry("GweeP-MW95j", "stern", ["stern"]);
        lookup.UpsertEntry("G5po2-MeP6B", "sega",  ["sega"]);

        var removed = lookup.RemoveEntry("GweeP-MW95j");

        Assert.True(removed);
        Assert.Single(lookup.OpdbIds);
        Assert.Single(lookup.MatchTokens!);
        Assert.Equal("G5po2-MeP6B", lookup.OpdbIds[0]);
        Assert.Equal(["sega"], lookup.MatchTokens![0]);
    }

    [Fact]
    public void UpsertEntry_MultiTokenManufacturer_StoresAllTokens()
    {
        var lookup = new MachineTitleLookup { Id = "pirates of the caribbean", PartitionKey = "pirates of the caribbean" };
        lookup.UpsertEntry("GR7ZX-MQ23b", "stern", ["stern"]);
        lookup.UpsertEntry("GRbPY-MePOP", "jjp",   ["jjp", "jersey", "jack"]);

        Assert.Equal(["jjp", "jersey", "jack"], lookup.MatchTokens![1]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```powershell
cd c:\projects\PinballWizard
dotnet test tests/PinballWizard.Scraper.Tests --filter "FullyQualifiedName~MachineTitleLookupTests" --no-build 2>&1 | Select-Object -Last 20
```

Expected: build errors — `UpsertEntry` doesn't accept 3 arguments yet, `MatchTokens` property doesn't exist.

- [ ] **Step 3: Add `MatchTokens` property and extend `UpsertEntry` / `RemoveEntry`**

In `src/PinballWizard.Core/Domain/MachineTitleLookup.cs`, make the following changes:

**Add the property** after the `Manufacturers` property (around line 63):

```csharp
/// <summary>
/// Pre-computed user-typeable tokens for each manufacturer entry, index-aligned
/// with <see cref="OpdbIds"/> and <see cref="Manufacturers"/>. Populated at
/// OPDB sync time by <c>OpdbMachineMapper.GetMatchTokens</c>. Used by
/// <c>MachineGroundingTool.ScoreEntryAgainstTokens</c> instead of scoring
/// the raw manufacturer key, so abbreviated keys like <c>"jjp"</c> match user
/// input like "Jersey Jack". Null for rows written before this feature deployed
/// (backfilled on the next OPDB sync); callers must treat null as "fall back to
/// key-as-single-token."
/// </summary>
[JsonPropertyName("matchTokens")]
public List<List<string>>? MatchTokens { get; set; }
```

**Replace `UpsertEntry`** (the entire method body, starting at the `public void UpsertEntry` line):

```csharp
/// <summary>
/// Add or replace an entry for <paramref name="opdbId"/>. If the
/// id is already present, the existing triple is removed and the
/// new triple appended (insertion-order — first-seen first). Keeps
/// <see cref="OpdbIds"/>, <see cref="Manufacturers"/>, and
/// <see cref="MatchTokens"/> consistent.
/// </summary>
public void UpsertEntry(string opdbId, string manufacturer, IReadOnlyList<string> matchTokens)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);
    ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
    ArgumentNullException.ThrowIfNull(matchTokens);

    MatchTokens ??= [];

    var idx = OpdbIds.IndexOf(opdbId);
    if (idx >= 0)
    {
        OpdbIds.RemoveAt(idx);
        Manufacturers.RemoveAt(idx);
        MatchTokens.RemoveAt(idx);
    }
    OpdbIds.Add(opdbId);
    Manufacturers.Add(manufacturer);
    MatchTokens.Add([.. matchTokens]);
}
```

**Replace `RemoveEntry`** (the entire method body):

```csharp
/// <summary>
/// Remove an entry for <paramref name="opdbId"/>. Returns
/// <c>true</c> if a triple was removed, <c>false</c> if the id was
/// not present.
/// </summary>
public bool RemoveEntry(string opdbId)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);
    var idx = OpdbIds.IndexOf(opdbId);
    if (idx < 0)
    {
        return false;
    }
    OpdbIds.RemoveAt(idx);
    Manufacturers.RemoveAt(idx);
    MatchTokens?.RemoveAt(idx);
    return true;
}
```

- [ ] **Step 4: Run test to verify it passes**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FullyQualifiedName~MachineTitleLookupTests" 2>&1 | Select-Object -Last 20
```

Expected: all 4 tests PASS.

- [ ] **Step 5: Run the full test suite to verify nothing is broken**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Select-Object -Last 30
```

Expected: build errors — `UpsertEntry` call sites in `OpdbSyncService` don't pass the third argument yet. That's fine — we'll fix that in Task 3. If you see build errors only (not test failures from running tests), that's expected at this stage.

- [ ] **Step 6: Commit**

```powershell
git add src/PinballWizard.Core/Domain/MachineTitleLookup.cs
git add tests/PinballWizard.Scraper.Tests/Domain/MachineTitleLookupTests.cs
git commit -m "feat(domain) AB#259: extend MachineTitleLookup.UpsertEntry with MatchTokens parallel array"
```

---

## Task 2: Add `ManufacturerMatchTokens` table to `OpdbMachineMapper`

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs`
- Test: `tests/PinballWizard.Scraper.Tests/Integrations/Opdb/OpdbMachineMapperTests.cs`

- [ ] **Step 1: Write the failing tests for `GetMatchTokens`**

Find the mapper test file:
```powershell
Get-ChildItem tests\PinballWizard.Scraper.Tests -Recurse -Filter "*MapperTests*"
```

Add to that file (or create `tests/PinballWizard.Scraper.Tests/Integrations/Opdb/OpdbMachineMapperTests.cs` if it doesn't exist):

```csharp
// These tests go in the existing OpdbMachineMapperTests class.
// If you need to create the file, use this namespace:
// namespace PinballWizard.Scraper.Tests.Integrations.Opdb;

[Theory]
[InlineData("stern",           new[] { "stern" })]
[InlineData("jjp",            new[] { "jjp", "jersey", "jack" })]
[InlineData("americanpinball",new[] { "americanpinball", "american", "pinball", "ap" })]
[InlineData("spooky",         new[] { "spooky" })]
[InlineData("multimorphic",   new[] { "multimorphic" })]
[InlineData("cgc",            new[] { "cgc", "chicago", "gaming" })]
[InlineData("haggis",         new[] { "haggis" })]
[InlineData("pinballbrothers",new[] { "pinballbrothers", "pinball", "brothers", "pb" })]
[InlineData("dutch",          new[] { "dutch" })]
[InlineData("barrelsoffun",   new[] { "barrelsoffun", "barrels", "fun", "bof" })]
public void GetMatchTokens_KnownKey_ReturnsExpectedTokens(string key, string[] expected)
{
    var tokens = OpdbMachineMapper.GetMatchTokens(key);
    Assert.Equal(expected, tokens);
}

[Fact]
public void GetMatchTokens_UnknownKey_ReturnsKeyAsSingleElement()
{
    var tokens = OpdbMachineMapper.GetMatchTokens("somelongtailmanufacturer");
    Assert.Equal(["somelongtailmanufacturer"], tokens);
}

[Fact]
public void GetMatchTokens_ReturnsReadOnlyList()
{
    // Callers must not mutate the returned list (it's the static table entry).
    // Verify the return type is IReadOnlyList<string>.
    IReadOnlyList<string> tokens = OpdbMachineMapper.GetMatchTokens("stern");
    Assert.NotNull(tokens);
}
```

- [ ] **Step 2: Run test to verify it fails**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FullyQualifiedName~GetMatchTokens" 2>&1 | Select-Object -Last 15
```

Expected: build error — `GetMatchTokens` does not exist yet.

- [ ] **Step 3: Add `ManufacturerMatchTokens` table and `GetMatchTokens` method**

In `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs`, add the following **after the closing brace of `NormalizeManufacturerKey`** (around line 107):

```csharp
// Pre-computed user-typeable token sets per manufacturer key.
// Keyed by the value NormalizeManufacturerKey returns.
// Multi-word/abbreviated keys get all forms a user might type;
// single-word keys get a one-element list (schema consistency).
private static readonly Dictionary<string, IReadOnlyList<string>> ManufacturerMatchTokens =
    new(StringComparer.Ordinal)
    {
        ["stern"]           = ["stern"],
        ["jjp"]             = ["jjp", "jersey", "jack"],
        ["americanpinball"] = ["americanpinball", "american", "pinball", "ap"],
        ["spooky"]          = ["spooky"],
        ["multimorphic"]    = ["multimorphic"],
        ["cgc"]             = ["cgc", "chicago", "gaming"],
        ["haggis"]          = ["haggis"],
        ["pinballbrothers"] = ["pinballbrothers", "pinball", "brothers", "pb"],
        ["dutch"]           = ["dutch"],
        ["barrelsoffun"]    = ["barrelsoffun", "barrels", "fun", "bof"],
    };

/// <summary>
/// Returns the pre-computed user-typeable token set for a manufacturer key.
/// Used by <c>OpdbSyncService</c> when writing <c>MachineTitleLookup.MatchTokens</c>
/// entries so the grounding tool can score abbreviated keys (e.g., <c>"jjp"</c>)
/// against natural-language input like "Jersey Jack Pirates".
/// Falls back to a single-element list containing the key itself for any
/// key not in the table (long-tail manufacturers from the <c>Sanitize</c> fallback).
/// </summary>
public static IReadOnlyList<string> GetMatchTokens(string key)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(key);
    return ManufacturerMatchTokens.TryGetValue(key, out var tokens)
        ? tokens
        : [key];
}
```

- [ ] **Step 4: Run test to verify it passes**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FullyQualifiedName~GetMatchTokens" 2>&1 | Select-Object -Last 15
```

Expected: all 12 tests PASS.

- [ ] **Step 5: Commit (build still broken until Task 3)**

```powershell
git add src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs
git add tests/PinballWizard.Scraper.Tests/Integrations/Opdb/OpdbMachineMapperTests.cs
git commit -m "feat(infra) AB#259: add ManufacturerMatchTokens table and GetMatchTokens to OpdbMachineMapper"
```

---

## Task 3: Wire `GetMatchTokens` into `OpdbSyncService.UpdateTitleLookupAsync`

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs`

The call site is in `UpdateTitleLookupAsync` (around line 512):
```csharp
lookup.UpsertEntry(machineId, manufacturer);
```

This must become:
```csharp
lookup.UpsertEntry(machineId, manufacturer, OpdbMachineMapper.GetMatchTokens(manufacturer));
```

There is exactly one call site — the `UpsertEntry` inside `UpdateTitleLookupAsync`. The second call site is `RemoveEntry`, which has no match-tokens argument.

- [ ] **Step 1: There is no new test to write** — the behavior change here is "data stored in Cosmos has a new field." The correctness is tested by the integration tests that verify the full sync + grounding flow. The unit-level invariant is already covered in Task 1's `MachineTitleLookupTests`. Skip ahead to the implementation.

- [ ] **Step 2: Update the `UpsertEntry` call in `UpdateTitleLookupAsync`**

Find line ~512 in `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs`:
```csharp
lookup.UpsertEntry(machineId, manufacturer);
```

Replace with:
```csharp
lookup.UpsertEntry(machineId, manufacturer, OpdbMachineMapper.GetMatchTokens(manufacturer));
```

No other changes needed in this file.

- [ ] **Step 3: Build to verify the solution compiles**

```powershell
dotnet build PinballWizard.slnx 2>&1 | Select-Object -Last 20
```

Expected: 0 errors, 0 warnings (or only pre-existing warnings — no new ones).

- [ ] **Step 4: Run full test suite**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Select-Object -Last 30
```

Expected: all tests PASS. The `MachineGroundingToolTests` that mock `IMachineTitleLookupRepository` may need updating — their mock `MachineTitleLookup` objects don't set `MatchTokens`. Those are fixed in Task 4.

- [ ] **Step 5: Commit**

```powershell
git add src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs
git commit -m "feat(infra) AB#259: pass GetMatchTokens to UpsertEntry in OpdbSyncService"
```

---

## Task 4: Update `MachineGroundingTool.ScoreEntryAgainstTokens` to use stored tokens

**Files:**
- Modify: `src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs`
- Modify: `tests/PinballWizard.Scraper.Tests/Ai/MachineGroundingToolTests.cs`

This is the core query-time change. The scorer must use `MatchTokens[i]` instead of `Manufacturers[i]`, with a fallback for pre-backfill rows.

- [ ] **Step 1: Write the failing tests**

In `tests/PinballWizard.Scraper.Tests/Ai/MachineGroundingToolTests.cs`, add:

```csharp
// --- ScoreEntryAgainstTokens (updated signature) ---

[Fact]
public void ScoreEntryAgainstTokens_SingleToken_ExactMatch_ReturnsOne()
{
    var score = MachineGroundingTool.ScoreEntryAgainstTokens(
        matchTokens: ["stern"],
        titleTokens: ["stern", "godzilla"]);
    Assert.Equal(1, score);
}

[Fact]
public void ScoreEntryAgainstTokens_MultiToken_PartialMatch_ReturnsMatchCount()
{
    // "jersey" is in matchTokens; "jack" is in matchTokens; "pirates" is not.
    var score = MachineGroundingTool.ScoreEntryAgainstTokens(
        matchTokens: ["jjp", "jersey", "jack"],
        titleTokens: ["jersey", "jack", "pirates"]);
    Assert.Equal(2, score);
}

[Fact]
public void ScoreEntryAgainstTokens_NoOverlap_ReturnsZero()
{
    var score = MachineGroundingTool.ScoreEntryAgainstTokens(
        matchTokens: ["cgc", "chicago", "gaming"],
        titleTokens: ["stern", "godzilla"]);
    Assert.Equal(0, score);
}

[Fact]
public void ScoreEntryAgainstTokens_JjpVsStern_JerseyJackWins()
{
    // "Jersey Jack Pirates" → tokens ["jersey", "jack", "pirates"]
    // jjp matchTokens = ["jjp", "jersey", "jack"] → score 2
    // stern matchTokens = ["stern"] → score 0
    var jjpScore = MachineGroundingTool.ScoreEntryAgainstTokens(
        matchTokens: ["jjp", "jersey", "jack"],
        titleTokens: ["jersey", "jack", "pirates"]);
    var sternScore = MachineGroundingTool.ScoreEntryAgainstTokens(
        matchTokens: ["stern"],
        titleTokens: ["jersey", "jack", "pirates"]);
    Assert.True(jjpScore > sternScore);
}

[Fact]
public void ScoreEntryAgainstTokens_CgcVsBally_ChicagoGamingWins()
{
    // "Chicago Gaming Attack from Mars" → tokens ["chicago", "gaming", "attack", "mars"]
    // cgc matchTokens = ["cgc", "chicago", "gaming"] → score 2
    // bally matchTokens = ["bally"] → score 0
    var cgcScore = MachineGroundingTool.ScoreEntryAgainstTokens(
        matchTokens: ["cgc", "chicago", "gaming"],
        titleTokens: ["chicago", "gaming", "attack", "mars"]);
    var ballyScore = MachineGroundingTool.ScoreEntryAgainstTokens(
        matchTokens: ["bally"],
        titleTokens: ["chicago", "gaming", "attack", "mars"]);
    Assert.True(cgcScore > ballyScore);
}

// --- GetMachineByTitleAsync with MatchTokens (integration-level mocks) ---

[Fact]
public async Task GetMachineByTitleAsync_JjpQualifier_PicksJjpOverStern()
{
    // Lookup row: Pirates of the Caribbean has two entries — stern and jjp
    var lookup = new MachineTitleLookup
    {
        Id = "pirates of the caribbean",
        PartitionKey = "pirates of the caribbean",
    };
    lookup.UpsertEntry("GR7ZX-MQ23b", "stern", ["stern"]);
    lookup.UpsertEntry("GRbPY-MePOP", "jjp",   ["jjp", "jersey", "jack"]);

    var sternMachine = new Machine
    {
        Id = "GR7ZX-MQ23b", PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball", Title = "Pirates of the Caribbean",
        Year = 2006, GroupId = null, Designers = [], Themes = [], Editions = [],
        ManufacturerSlugs = [], OpdbSourceUrl = "https://opdb.org/machines/GR7ZX-MQ23b",
        FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
    };
    var jjpMachine = new Machine
    {
        Id = "GRbPY-MePOP", PartitionKey = "jjp",
        ManufacturerDisplayName = "Jersey Jack Pinball", Title = "Pirates of the Caribbean",
        Year = 2019, GroupId = null, Designers = [], Themes = [], Editions = [],
        ManufacturerSlugs = [], OpdbSourceUrl = "https://opdb.org/machines/GRbPY-MePOP",
        FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
    };

    var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
    titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<MachineTitleLookup?>(lookup));

    var machines = Substitute.For<IMachineRepository>();
    // When the tool resolves index 1 (jjp), it point-reads jjp machine
    machines.GetByOpdbIdAsync("GRbPY-MePOP", "jjp", Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<Machine?>(jjpMachine));
    machines.GetByOpdbIdAsync("GR7ZX-MQ23b", "stern", Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<Machine?>(sternMachine));
    machines.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable.Empty<Machine>());

    var tool = new MachineGroundingTool(machines, titleLookups, NullLogger.Instance);

    // "Jersey Jack Pirates of the Caribbean" → tokens include "jersey" and "jack"
    var result = await tool.GetMachineByTitleAsync("Jersey Jack Pirates of the Caribbean", CancellationToken.None);

    Assert.NotNull(result);
    Assert.Equal("GRbPY-MePOP", result!.OpdbId);
    Assert.Equal("Jersey Jack Pinball", result.Manufacturer);
}

[Fact]
public async Task GetMachineByTitleAsync_NullMatchTokens_FallsBackToManufacturerKeyScoring()
{
    // Pre-backfill row: MatchTokens is null. Scorer must fall back to key-as-single-token.
    var lookup = new MachineTitleLookup
    {
        Id = "godzilla",
        PartitionKey = "godzilla",
        OpdbIds = ["G5po2-MeP6B", "GweeP-MW95j"],
        Manufacturers = ["sega", "stern"],
        MatchTokens = null,   // simulates old row without MatchTokens
    };

    var sternMachine = new Machine
    {
        Id = "GweeP-MW95j", PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla",
        Year = 2021, GroupId = "GweeP", Designers = [], Themes = [], Editions = [],
        ManufacturerSlugs = [], OpdbSourceUrl = "https://opdb.org/machines/GweeP-MW95j",
        FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
    };

    var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
    titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<MachineTitleLookup?>(lookup));

    var machines = Substitute.For<IMachineRepository>();
    machines.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<Machine?>(sternMachine));
    machines.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable.Empty<Machine>());

    var tool = new MachineGroundingTool(machines, titleLookups, NullLogger.Instance);

    // "Stern Godzilla" — even without MatchTokens, the fallback key "stern" matches the token
    var result = await tool.GetMachineByTitleAsync("Stern Godzilla", CancellationToken.None);

    Assert.NotNull(result);
    Assert.Equal("GweeP-MW95j", result!.OpdbId);
}
```

- [ ] **Step 2: Run test to verify it fails**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FullyQualifiedName~ScoreEntryAgainstTokens|FullyQualifiedName~JjpQualifier|FullyQualifiedName~NullMatchTokens" 2>&1 | Select-Object -Last 20
```

Expected: build errors — `ScoreEntryAgainstTokens` takes `(string, IReadOnlyList<string>)` not `(IReadOnlyList<string>, IReadOnlyList<string>)`.

- [ ] **Step 3: Update `ScoreEntryAgainstTokens` signature and implementation**

In `src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs`, replace the entire `ScoreEntryAgainstTokens` method:

```csharp
// Scores a collision-row entry against tokens extracted from the user-supplied
// title string. Returns +1 per titleToken that appears in matchTokens.
// Using the stored match-token set (rather than the raw manufacturer key)
// correctly handles abbreviated keys like "jjp" → ["jjp","jersey","jack"]
// so "Jersey Jack Pirates" scores 2 for the jjp entry vs 0 for stern.
// Zero means no signal — used as tie-break sentinel so bare franchise titles
// ("Godzilla" with no manufacturer qualifier) preserve insertion-order behaviour.
internal static int ScoreEntryAgainstTokens(
    IReadOnlyList<string> matchTokens,
    IReadOnlyList<string> titleTokens)
{
    var score = 0;
    foreach (var token in titleTokens)
    {
        foreach (var matchToken in matchTokens)
        {
            if (string.Equals(token, matchToken, StringComparison.Ordinal))
            {
                score++;
                break; // count each titleToken at most once per entry
            }
        }
    }
    return score;
}
```

- [ ] **Step 4: Update the call site in `GetMachineByTitleAsync`**

The collision-resolution block (around line 176–187) currently reads:
```csharp
var titleTokens = TokenizeForOverlap(title);
var bestIdx = 0;
var bestScore = ScoreEntryAgainstTokens(lookup!.Manufacturers[0], titleTokens);

for (var i = 1; i < lookup.OpdbIds.Count; i++)
{
    var score = ScoreEntryAgainstTokens(lookup.Manufacturers[i], titleTokens);
    if (score > bestScore)
    {
        bestScore = score;
        bestIdx = i;
    }
}
```

Replace with:
```csharp
var titleTokens = TokenizeForOverlap(title);
var bestIdx = 0;
// MatchTokens may be null for rows written before this feature deployed.
// Fallback: treat the raw manufacturer key as a single-element token list.
// The next OPDB sync backfills MatchTokens automatically.
var bestScore = ScoreEntryAgainstTokens(
    lookup!.MatchTokens?[0] ?? [lookup.Manufacturers[0]],
    titleTokens);

for (var i = 1; i < lookup.OpdbIds.Count; i++)
{
    var score = ScoreEntryAgainstTokens(
        lookup.MatchTokens?[i] ?? [lookup.Manufacturers[i]],
        titleTokens);
    if (score > bestScore)
    {
        bestScore = score;
        bestIdx = i;
    }
}
```

Also update the comment above `ScoreEntryAgainstTokens` to reference the new approach (find the block comment starting with "Score every collision-row entry…" and replace it):

```csharp
// Score every collision-row entry against manufacturer tokens extracted from
// the input title. Uses the stored MatchTokens for each entry — which expand
// abbreviated keys (e.g., "jjp" → ["jjp","jersey","jack"]) — so user input
// like "Jersey Jack Pirates" correctly outscores "stern". For pre-backfill rows
// where MatchTokens is null, falls back to the raw manufacturer key as a
// single-element list, preserving pre-feature behaviour. Ties (all-zero or
// equal scores) preserve insertion order — backward-compatible with the
// pre-scoring first-hit behaviour for bare franchise titles ("Godzilla").
```

- [ ] **Step 5: Update any existing `ScoreEntryAgainstTokens` tests that use the old signature**

Search for old-signature usages:
```powershell
Select-String -Path tests\PinballWizard.Scraper.Tests\Ai\MachineGroundingToolTests.cs -Pattern "ScoreEntryAgainstTokens"
```

The existing tests call the old `(string manufacturerKey, IReadOnlyList<string> titleTokens)` form. Update each one to pass a `IReadOnlyList<string>` as the first argument. For example:

Old:
```csharp
var score = MachineGroundingTool.ScoreEntryAgainstTokens("stern", titleTokens);
```
New:
```csharp
var score = MachineGroundingTool.ScoreEntryAgainstTokens(["stern"], titleTokens);
```

For the "SternGodzillaScenario" and "CollisionWithManufacturerQualifier" tests that build a full `MachineTitleLookup` mock: make sure the lookup objects set `MatchTokens`. The easiest approach is to call `UpsertEntry` with the three-argument form, which populates `MatchTokens` automatically.

- [ ] **Step 6: Run the new tests**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FullyQualifiedName~ScoreEntryAgainstTokens|FullyQualifiedName~JjpQualifier|FullyQualifiedName~NullMatchTokens" 2>&1 | Select-Object -Last 20
```

Expected: all PASS.

- [ ] **Step 7: Run the full test suite**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Select-Object -Last 30
```

Expected: all tests PASS.

- [ ] **Step 8: Commit**

```powershell
git add src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs
git add tests/PinballWizard.Scraper.Tests/Ai/MachineGroundingToolTests.cs
git commit -m "feat(app) AB#259: score MatchTokens in MachineGroundingTool — fixes jjp/cgc/americanpinball disambiguation"
```

---

## Task 5: Update `MatchTokens`-length guard in `MachineGroundingTool`

**Files:**
- Modify: `src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs`

The existing length guard (added in PR #293) checks `OpdbIds.Count != Manufacturers.Count`. Now that there are three parallel arrays, the guard must also verify `MatchTokens` length when `MatchTokens` is non-null.

- [ ] **Step 1: Write a failing test for the extended guard**

In `tests/PinballWizard.Scraper.Tests/Ai/MachineGroundingToolTests.cs` add:

```csharp
[Fact]
public async Task GetMachineByTitleAsync_MismatchedMatchTokensLength_FallsBackToCrossPartition()
{
    // Simulates a corruption where MatchTokens has wrong length (2 opdbIds, 1 matchTokens entry)
    var lookup = new MachineTitleLookup
    {
        Id = "godzilla",
        PartitionKey = "godzilla",
        OpdbIds = ["G5po2-MeP6B", "GweeP-MW95j"],
        Manufacturers = ["sega", "stern"],
        MatchTokens = [["sega"]],   // length 1, mismatched with OpdbIds length 2
    };

    var sternMachine = new Machine
    {
        Id = "GweeP-MW95j", PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla",
        Year = 2021, GroupId = null, Designers = [], Themes = [], Editions = [],
        ManufacturerSlugs = [], OpdbSourceUrl = "https://opdb.org/machines/GweeP-MW95j",
        FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
    };

    var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
    titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<MachineTitleLookup?>(lookup));

    var machines = Substitute.For<IMachineRepository>();
    // Cross-partition fallback is QueryByTitleAsync
    machines.QueryByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable.Return(sternMachine));
    machines.GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(AsyncEnumerable.Empty<Machine>());

    var logger = new TestLogger<MachineGroundingTool>();
    var tool = new MachineGroundingTool(machines, titleLookups, logger);

    var result = await tool.GetMachineByTitleAsync("Godzilla", CancellationToken.None);

    // Falls back to cross-partition and gets a result
    Assert.NotNull(result);
    // A warning was logged about the mismatch
    Assert.Contains(logger.LoggedMessages, m => m.Contains("mismatched"));
}
```

> **Note:** `AsyncEnumerable.Return` is a helper. If your test project doesn't have it, use:
> ```csharp
> async IAsyncEnumerable<Machine> Yield(Machine m) { yield return m; }
> machines.QueryByTitleAsync(...)
>     .Returns(Yield(sternMachine));
> ```

- [ ] **Step 2: Run test to verify it fails**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FullyQualifiedName~MismatchedMatchTokensLength" 2>&1 | Select-Object -Last 15
```

Expected: FAIL — currently the guard only checks OpdbIds vs Manufacturers, doesn't catch MatchTokens mismatch.

- [ ] **Step 3: Extend the length guard**

In `src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs`, find the existing guard block (around line 160):

```csharp
if (lookupHit && lookup!.OpdbIds.Count != lookup.Manufacturers.Count)
```

Replace the entire guard block with:

```csharp
// Guard: all three parallel arrays must be the same length (maintained by
// UpsertEntry / RemoveEntry). MatchTokens can legitimately be null for rows
// written before this feature deployed — that's handled by the null-coalesce
// fallback in the scorer. But a non-null MatchTokens with wrong length
// indicates corruption (direct Cosmos edit, partial write, etc.).
var matchTokensLengthOk = lookup!.MatchTokens is null
    || lookup.MatchTokens.Count == lookup.OpdbIds.Count;

if (lookupHit && (lookup.OpdbIds.Count != lookup.Manufacturers.Count || !matchTokensLengthOk))
{
    _logger.LogWarning(
        "MachineGroundingTool: lookup row for '{Title}' has mismatched array lengths — OpdbIds={OpdbCount}, Manufacturers={ManufacturerCount}, MatchTokens={MatchTokensCount}. Possible data corruption. Falling back to cross-partition query. Re-run OPDB sync to remediate.",
        title,
        lookup.OpdbIds.Count,
        lookup.Manufacturers.Count,
        lookup.MatchTokens?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");
    lookupHit = false;
}
```

- [ ] **Step 4: Run test to verify it passes**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests --filter "FullyQualifiedName~MismatchedMatchTokensLength" 2>&1 | Select-Object -Last 15
```

Expected: PASS.

- [ ] **Step 5: Run full suite**

```powershell
dotnet test tests/PinballWizard.Scraper.Tests 2>&1 | Select-Object -Last 30
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs
git add tests/PinballWizard.Scraper.Tests/Ai/MachineGroundingToolTests.cs
git commit -m "feat(app) AB#259: extend MatchTokens length guard in MachineGroundingTool"
```

---

## Task 6: ADR update and PR

**Files:**
- Modify: `docs/adr/0025-cosmos-for-user-delight.md` (or the relevant ADR)

- [ ] **Step 1: Open the ADR and add a brief amendment note**

Find the section in `docs/adr/0025-cosmos-for-user-delight.md` that describes the `machine_title_lookups` schema. Add a note like:

```markdown
**Amendment (PR #XXX, 2026-05-26):** `machine_title_lookups` entries now carry a third parallel array `matchTokens` populated at OPDB sync time by `OpdbMachineMapper.GetMatchTokens`. This pre-computes user-typeable token sets for abbreviated/compound manufacturer keys (e.g., `"jjp"` → `["jjp","jersey","jack"]`) so the grounding tool's collision scorer handles all 10 manufacturer keys correctly. Rows written before this change have `matchTokens=null`; the scorer falls back to the raw key as a single-element list, preserving pre-feature behaviour during the backfill window.
```

- [ ] **Step 2: Run the pre-PR local review**

```powershell
python $env:USERPROFILE\.claude\bin\local-pr-review.py
```

Expected: gate set, no blocking findings.

- [ ] **Step 3: Create the PR**

```powershell
gh pr create `
  --title "feat(rag) AB#259: Option B — pre-computed MatchTokens for manufacturer disambiguation" `
  --body "$(cat <<'EOF'
## Summary

- Extends `MachineTitleLookup` with a `MatchTokens` third parallel array, populated at OPDB sync time
- Adds `OpdbMachineMapper.GetMatchTokens` with expansion table covering all 10 manufacturer keys (`jjp` → `jersey jack`, `cgc` → `chicago gaming`, etc.)
- Updates `MachineGroundingTool.ScoreEntryAgainstTokens` to score against stored match tokens instead of the raw key
- Backward-compatible: null MatchTokens (pre-backfill rows) fall back to key-as-single-token; next OPDB sync auto-backfills

## Motivation

Live catalog probe (1,603 rows, 367 collisions) showed 174 cross-manufacturer collisions. Single-word keys (stern, gottlieb, sega) already score correctly. The ~9 collisions involving abbreviation/compound keys (jjp, cgc, americanpinball, pinballbrothers, barrelsoffun) could not be disambiguated at query time because `"jjp"` never appears in user input like "Jersey Jack Pirates." Option B pre-computes the expansion once at write time so query-time scoring works universally.

## Test plan
- [ ] `MachineTitleLookupTests` — UpsertEntry/RemoveEntry with 3 arrays
- [ ] `OpdbMachineMapperTests.GetMatchTokens` — all 10 known keys + fallback
- [ ] `MachineGroundingToolTests.ScoreEntryAgainstTokens` — new IReadOnlyList signature
- [ ] `MachineGroundingToolTests.GetMachineByTitleAsync_JjpQualifier` — end-to-end JJP vs Stern
- [ ] `GetMachineByTitleAsync_NullMatchTokens_FallsBackToManufacturerKeyScoring` — backward compat
- [ ] `GetMachineByTitleAsync_MismatchedMatchTokensLength` — extended guard

🤖 Generated with [Claude Code](https://claude.ai/claude-code)
EOF
)"
```

---

## Self-Review

### Spec coverage

| Requirement | Covered by |
|---|---|
| Add `MatchTokens` to `MachineTitleLookup` | Task 1 |
| `UpsertEntry` keeps 3 arrays in sync | Task 1 |
| `RemoveEntry` keeps 3 arrays in sync | Task 1 |
| `GetMatchTokens` expansion table for all 10 keys | Task 2 |
| Fallback for unknown/long-tail keys | Task 2 |
| `OpdbSyncService` passes tokens to `UpsertEntry` | Task 3 |
| Scorer uses `MatchTokens` at query time | Task 4 |
| Null `MatchTokens` fallback for pre-backfill rows | Task 4 |
| Extended length guard covers `MatchTokens` | Task 5 |
| ADR updated | Task 6 |
| JJP vs Stern disambiguation test | Task 4 |
| CGC vs Bally disambiguation test | Task 4 |
| Backward-compat (null MatchTokens) test | Task 4 |

### Placeholder scan

No "TBD", "TODO", "implement later", or vague steps found. Every code block is complete.

### Type consistency

- `UpsertEntry(string, string, IReadOnlyList<string>)` — defined Task 1, used Task 3, called in tests Task 1 and Task 4. ✓
- `GetMatchTokens(string) → IReadOnlyList<string>` — defined Task 2, used Task 3. ✓
- `ScoreEntryAgainstTokens(IReadOnlyList<string>, IReadOnlyList<string>) → int` — defined Task 4, tested Task 4. ✓
- `lookup.MatchTokens?[i] ?? [lookup.Manufacturers[i]]` — `MatchTokens` is `List<List<string>>?`, indexer yields `List<string>`, used as `IReadOnlyList<string>`. ✓
