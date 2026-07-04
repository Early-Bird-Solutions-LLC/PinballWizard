# Tilt Forums rulesheet edition-family fan-out Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `TiltForumsGameMatcher` fan a rulesheet out to every sibling edition (Pro/Premium/LE)
of the same base game instead of reporting a multi-match as unmatched, and tag every synthesis-pipeline
rulesheet chunk (Tilt Forums and Kineticist) with `EditionScope: "franchise-wide"`.

**Architecture:** Extract the existing `DocumentLinker.IsEditionFamily` GroupId+Year predicate to a
shared `PinballWizard.Core.Domain.EditionFamily` static helper. `TiltForumsGameMatcher` gains a new
`ResolvedEditionFamily` status and calls `IMachineRepository.GetSiblingsByGroupIdAsync` to build the
full sibling set when a multi-match is a genuine edition family. Both CLI sync verbs
(`--sync-tiltforums-rulesheets`, `--sync-kineticist-tutorials`) tag every `ChunkRequest` with
`EditionScope: "franchise-wide"`.

**Tech Stack:** .NET 10, xUnit + NSubstitute (tests), existing `IMachineRepository`, existing
`ChunkRequest`/`IRagIndexer`.

## Global Constraints

- Never guess on a genuine cross-game title collision (different `GroupId`s) — it must still land
  in the unmatched/ambiguous bucket (Invariant #17, "fallbacks must not hide failures").
- `DocumentLinker`'s existing test suite (`tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs`)
  must pass unmodified after the Task 2 refactor — it is the regression safety net proving the
  extracted helper preserves current behavior, including the current `main`'s
  `candidates.Count == 0` guard (not the older `< 2` guard — see the design spec's amendment note).
- Personal-identity commits only (`Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`),
  no Claude attribution trailer.
- No premature abstraction: no new CLI flags, no new `IngestionSource` entries, no changes to
  `EditionResolver`/`ResolveEditionFamily` (the PDF-pipeline fan-out machinery is untouched).
- Design reference: [`docs/superpowers/specs/2026-07-04-tiltforums-edition-family-fanout-design.md`](../specs/2026-07-04-tiltforums-edition-family-fanout-design.md).

---

## Task 1: `EditionFamily` shared domain helper

**Files:**
- Create: `src/PinballWizard.Core/Domain/EditionFamily.cs`
- Test: `tests/PinballWizard.Core.Tests/Domain/EditionFamilyTests.cs`

**Interfaces:**
- Produces: `PinballWizard.Core.Domain.EditionFamily.IsEditionFamily(IReadOnlyList<Machine> candidates) : bool`
  — consumed by Task 2 (`DocumentLinker`) and Task 3 (`TiltForumsGameMatcher`).

- [ ] **Step 1: Write the failing tests**

```csharp
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Core.Tests.Domain;

public sealed class EditionFamilyTests
{
    private static Machine MakeMachine(string id, string? groupId, int? year) => new()
    {
        Id = id,
        PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball",
        Title = "Some Game",
        GroupId = groupId,
        Year = year,
    };

    [Fact]
    public void IsEditionFamily_SameGroupIdSameYear_ReturnsTrue()
    {
        var pro = MakeMachine("GweeP-MW95j", "GweeP", 2021);
        var premium = MakeMachine("GweeP-Ml9pZ", "GweeP", 2021);

        Assert.True(EditionFamily.IsEditionFamily([pro, premium]));
    }

    [Fact]
    public void IsEditionFamily_SameGroupIdDifferentYear_ReturnsFalse()
    {
        // An unrelated reissue/remake can reuse the same group segment in a
        // different year — that is NOT the same edition family.
        var original = MakeMachine("ABCD-1", "ABCD", 1992);
        var remake = MakeMachine("ABCD-2", "ABCD", 2023);

        Assert.False(EditionFamily.IsEditionFamily([original, remake]));
    }

    [Fact]
    public void IsEditionFamily_DifferentGroupId_ReturnsFalse()
    {
        var sternGodzilla = MakeMachine("GweeP-MW95j", "GweeP", 2021);
        var segaGodzilla = MakeMachine("G4O1L-abc12", "G4O1L", 1998);

        Assert.False(EditionFamily.IsEditionFamily([sternGodzilla, segaGodzilla]));
    }

    [Fact]
    public void IsEditionFamily_SingleCandidateWithGroupIdAndYear_ReturnsTrue()
    {
        // A lone candidate that belongs to a group still counts — matches
        // current DocumentLinker usage, which runs a singleton through this
        // check to tag EditionScope.SingleEdition vs. FranchiseWide correctly.
        var solo = MakeMachine("GweeP-MW95j", "GweeP", 2021);

        Assert.True(EditionFamily.IsEditionFamily([solo]));
    }

    [Fact]
    public void IsEditionFamily_EmptyList_ReturnsFalse()
    {
        Assert.False(EditionFamily.IsEditionFamily([]));
    }

    [Fact]
    public void IsEditionFamily_NullGroupId_ReturnsFalse()
    {
        var a = MakeMachine("A-1", null, 2021);
        var b = MakeMachine("A-2", null, 2021);

        Assert.False(EditionFamily.IsEditionFamily([a, b]));
    }

    [Fact]
    public void IsEditionFamily_NullYear_ReturnsFalse()
    {
        var a = MakeMachine("A-1", "GroupA", null);
        var b = MakeMachine("A-2", "GroupA", null);

        Assert.False(EditionFamily.IsEditionFamily([a, b]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Core.Tests/PinballWizard.Core.Tests.csproj --filter "FullyQualifiedName~EditionFamilyTests"`
Expected: FAIL — `EditionFamily` does not exist yet (compile error).

- [ ] **Step 3: Implement the helper**

```csharp
namespace PinballWizard.Core.Domain;

/// <summary>
/// Determines whether a set of catalog machines represents the same base
/// game released as multiple editions (Pro/Premium/LE) — the discriminator
/// between "fan a franchise-wide document out to every sibling" and
/// "genuinely different games that happen to share a title," per ADR-0032.
/// </summary>
public static class EditionFamily
{
    /// <summary>
    /// True when every candidate shares a single non-null <see cref="Machine.GroupId"/>
    /// AND a single non-null <see cref="Machine.Year"/>. The year guard separates
    /// genuine same-year edition siblings from an unrelated reissue/remake that
    /// happens to reuse the same group segment. A single candidate that carries
    /// a GroupId+Year also counts — used to correctly tag a lone edition's
    /// EditionScope as distinct from an ungrouped, standalone machine.
    /// </summary>
    public static bool IsEditionFamily(IReadOnlyList<Machine> candidates)
    {
        if (candidates.Count == 0) return false;
        var groupIds = candidates.Select(m => m.GroupId).Distinct().ToList();
        var years = candidates.Select(m => m.Year).Distinct().ToList();
        return groupIds.Count == 1 && groupIds[0] is not null
            && years.Count == 1 && years[0] is not null;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Core.Tests/PinballWizard.Core.Tests.csproj --filter "FullyQualifiedName~EditionFamilyTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Core/Domain/EditionFamily.cs tests/PinballWizard.Core.Tests/Domain/EditionFamilyTests.cs
git commit -m "feat(linking) add shared EditionFamily.IsEditionFamily domain helper"
```

---

## Task 2: `DocumentLinker.IsEditionFamily` delegates to the shared helper

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs:524-531`

**Interfaces:**
- Consumes: `PinballWizard.Core.Domain.EditionFamily.IsEditionFamily` (Task 1).
- No new public interface — `DocumentLinker.IsEditionFamily` keeps its existing private signature
  and all existing call sites (lines ~605, ~704, ~824) are unaffected.

This is a pure refactor with an existing regression suite — no new tests are added in this task.

- [ ] **Step 1: Confirm the current implementation and its callers**

Read `src/PinballWizard.Application/Linking/DocumentLinker.cs:519-531` and confirm it currently reads:

```csharp
private static bool IsEditionFamily(List<Machine> candidates)
{
    if (candidates.Count == 0) return false;
    var segments = candidates.Select(m => m.GroupId).Distinct().ToList();
    var years = candidates.Select(m => m.Year).Distinct().ToList();
    return segments.Count == 1 && segments[0] is not null
        && years.Count == 1 && years[0] is not null;
}
```

If the guard or logic differs from this (another concurrent change may have landed), stop and
reconcile with `PinballWizard.Core.Domain.EditionFamily.IsEditionFamily` before proceeding — the two
must stay semantically identical.

- [ ] **Step 2: Run the existing DocumentLinker tests to capture the current baseline**

Run: `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "FullyQualifiedName~DocumentLinkerTests"`
Expected: PASS (all existing tests green before the refactor).

- [ ] **Step 3: Replace the method body with a delegate**

Replace the method body found in Step 1 with:

```csharp
private static bool IsEditionFamily(List<Machine> candidates) => EditionFamily.IsEditionFamily(candidates);
```

Add `using PinballWizard.Core.Domain;` to the top of `DocumentLinker.cs` if not already present (it
is — `Machine` itself comes from that namespace).

- [ ] **Step 4: Run the DocumentLinker tests again to confirm no regression**

Run: `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "FullyQualifiedName~DocumentLinkerTests"`
Expected: PASS — identical results to Step 2.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs
git commit -m "refactor(linking) delegate DocumentLinker.IsEditionFamily to shared EditionFamily helper"
```

---

## Task 3: `TiltForumsGameMatcher` fan-out restructuring

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs`
- Modify: `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs`

**Interfaces:**
- Consumes: `PinballWizard.Core.Domain.EditionFamily.IsEditionFamily` (Task 1),
  `IMachineRepository.GetSiblingsByGroupIdAsync(string groupId, CancellationToken) : IAsyncEnumerable<Machine>`
  (existing, `src/PinballWizard.Application/Persistence/IMachineRepository.cs:50`).
- Produces: `TiltForumsGameMatchStatus.ResolvedEditionFamily` (new enum member),
  `TiltForumsMachineMatch(string MachineId, string MachineTitle, string ManufacturerDisplayName)`
  (new record), `TiltForumsGameMatchResult(TiltForumsGameMatchStatus Status, IReadOnlyList<TiltForumsMachineMatch> Machines)`
  (restructured record — **breaking change** to the existing `MachineId`/`MachineTitle`/
  `ManufacturerDisplayName` shape) — consumed by Task 4 (CLI verb).

- [ ] **Step 1: Rewrite the test file's assertions for the new result shape and add fan-out cases**

Replace the full contents of `tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs`:

```csharp
using NSubstitute;
using PinballWizard.Core.Domain;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.TiltForums;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.TiltForums;

public sealed class TiltForumsGameMatcherTests
{
    private static Machine MakeMachine(
        string id, string manufacturerKey, string manufacturerDisplayName, string title,
        string? groupId = null, int? year = null) => new()
    {
        Id = id,
        PartitionKey = manufacturerKey,
        ManufacturerDisplayName = manufacturerDisplayName,
        Title = title,
        GroupId = groupId,
        Year = year,
    };

    [Fact]
    public async Task ResolveAsync_SingleMatchInManufacturerPartition_ReturnsResolved()
    {
        var stern2021 = MakeMachine("GweeP-MW95j", "stern", "Stern Pinball", "Godzilla");
        var sega1998 = MakeMachine("G4O1L-abc12", "sega", "Sega", "Godzilla");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([stern2021, sega1998]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Godzilla", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.Single(result.Machines);
        Assert.Equal("GweeP-MW95j", result.Machines[0].MachineId);
        Assert.Equal("Godzilla", result.Machines[0].MachineTitle);
        Assert.Equal("Stern Pinball", result.Machines[0].ManufacturerDisplayName);
    }

    [Fact]
    public async Task ResolveAsync_NoMatchInManufacturerPartition_ReturnsNoMatch()
    {
        // "Star Wars" exists for Bally/Williams only — Stern has no machine
        // by this exact title, so this must NOT fall back to an unscoped
        // guess; it must report NoMatch.
        var williamsStarWars = MakeMachine("G4O1L-MDW47", "williams", "Williams", "Star Wars");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Star Wars", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([williamsStarWars]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Star Wars", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchesSameGroupAndYear_ReturnsEditionFamily_FansOutToFullSiblingSet()
    {
        // Two Stern Godzilla bases share GroupId "GweeP" and release year 2021 —
        // a genuine edition family. The title-matched candidates are the query
        // result, but the fan-out set must come from GetSiblingsByGroupIdAsync,
        // NOT just the title-matched candidates — proven here by a third sibling
        // ("Godzilla Collector's Edition") that carries different title text and
        // would never have matched the original QueryByTitleAsync("Godzilla") call.
        var pro = MakeMachine("GweeP-MW95j", "stern", "Stern Pinball", "Godzilla", "GweeP", 2021);
        var premium = MakeMachine("GweeP-Ml9pZ", "stern", "Stern Pinball", "Godzilla", "GweeP", 2021);
        var collectors = MakeMachine("GweeP-Xk2Qp", "stern", "Stern Pinball", "Godzilla Collector's Edition", "GweeP", 2021);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Godzilla", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([pro, premium]));
        repo.GetSiblingsByGroupIdAsync("GweeP", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([pro, premium, collectors]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Godzilla", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.ResolvedEditionFamily, result.Status);
        Assert.Equal(3, result.Machines.Count);
        Assert.Contains(result.Machines, m => m.MachineId == "GweeP-MW95j");
        Assert.Contains(result.Machines, m => m.MachineId == "GweeP-Ml9pZ");
        Assert.Contains(result.Machines, m => m.MachineId == "GweeP-Xk2Qp" && m.MachineTitle == "Godzilla Collector's Edition");
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchesDifferentGroups_ReturnsMultipleMatches_NotGuessed()
    {
        // Same title, same manufacturer partition, but genuinely different
        // games (different GroupId/Year) — must stay ambiguous, never fanned out.
        var edition1 = MakeMachine("ABCD-1", "stern", "Stern Pinball", "Some Game", "ABCD", 1994);
        var edition2 = MakeMachine("WXYZ-1", "stern", "Stern Pinball", "Some Game", "WXYZ", 2019);

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Some Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([edition1, edition2]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Some Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
        await repo.DidNotReceive().GetSiblingsByGroupIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchesMissingGroupOrYear_ReturnsMultipleMatches_NotGuessed()
    {
        // Two machines sharing a title in the same partition but with no
        // GroupId/Year data at all — cannot be proven an edition family, so
        // this must NOT be guessed as a fan-out either.
        var a = MakeMachine("ABCD-1", "stern", "Stern Pinball", "Some Game");
        var b = MakeMachine("ABCD-2", "stern", "Stern Pinball", "Some Game");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Some Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([a, b]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Some Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
    }

    [Fact]
    public async Task ResolveAsync_ManufacturerHeaderTextNormalized_MatchesPartitionKey()
    {
        // "Jersey Jack Pinball" (master-list header text) must normalize to
        // partition key "jjp" via the existing OpdbMachineMapper function.
        var jjpMachine = MakeMachine("JJP-1", "jjp", "Jersey Jack Pinball", "Wonka");

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Wonka", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([jjpMachine]));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Wonka", "Jersey Jack Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.Resolved, result.Status);
        Assert.Equal("JJP-1", result.Machines[0].MachineId);
    }

    [Fact]
    public async Task ResolveAsync_ZeroCandidatesAtAll_ReturnsNoMatch()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Nonexistent Game", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<Machine>()));

        var result = await TiltForumsGameMatcher.ResolveAsync(repo, "Nonexistent Game", "Stern Pinball", CancellationToken.None);

        Assert.Equal(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, result.Status);
        Assert.Empty(result.Machines);
    }

    private static async IAsyncEnumerable<Machine> ToAsyncEnumerable(IEnumerable<Machine> machines)
    {
        foreach (var machine in machines)
        {
            yield return machine;
        }
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsGameMatcherTests"`
Expected: FAIL — `TiltForumsGameMatchStatus.ResolvedEditionFamily`, `TiltForumsMachineMatch`, and
`result.Machines` do not exist yet (compile errors).

- [ ] **Step 3: Rewrite `TiltForumsGameMatcher.cs`**

Replace the full contents of `src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs`:

```csharp
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Integrations.Opdb;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// Outcome of resolving a Tilt Forums rulesheet's game title to catalog
/// <c>Machine</c>(s), scoped to the manufacturer the master list grouped it
/// under.
/// </summary>
public enum TiltForumsGameMatchStatus
{
    /// <summary>Exactly one machine matched the title within the resolved manufacturer partition.</summary>
    Resolved,

    /// <summary>Multiple machines matched, all in the same edition family (same GroupId+Year) — fanned out to every sibling via GetSiblingsByGroupIdAsync.</summary>
    ResolvedEditionFamily,

    /// <summary>No machine matched the title within the resolved manufacturer partition.</summary>
    NoMatchInManufacturerPartition,

    /// <summary>Multiple machines matched, NOT an edition family (different GroupIds/Years, or missing GroupId/Year data) — a genuine cross-game title collision. Not guessed.</summary>
    MultipleMatchesInManufacturerPartition,
}

/// <summary>One machine target a resolved rulesheet should be indexed against.</summary>
public sealed record TiltForumsMachineMatch(string MachineId, string MachineTitle, string ManufacturerDisplayName);

/// <summary>
/// Result of <see cref="TiltForumsGameMatcher.ResolveAsync"/>. <see cref="Machines"/> is empty for
/// <see cref="TiltForumsGameMatchStatus.NoMatchInManufacturerPartition"/> and
/// <see cref="TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition"/>, has exactly one
/// entry for <see cref="TiltForumsGameMatchStatus.Resolved"/>, and one entry per sibling edition
/// for <see cref="TiltForumsGameMatchStatus.ResolvedEditionFamily"/>.
/// </summary>
public sealed record TiltForumsGameMatchResult(
    TiltForumsGameMatchStatus Status,
    IReadOnlyList<TiltForumsMachineMatch> Machines);

/// <summary>
/// Resolves a Tilt Forums rulesheet's (title, manufacturer header text) pair
/// to one or more catalog <c>Machine</c>s.
/// </summary>
/// <remarks>
/// Every existing single-manufacturer scraper's HTTP client only ever
/// touches one manufacturer's site, so nothing in the codebase before this
/// has had to disambiguate a title across manufacturer partitions at
/// scrape/sync time — <c>IMachineTitleLookupRepository</c>'s own fallback
/// path takes the first OPDB id unscoped (see
/// <c>KineticistTutorialsClient</c>'s "legacy fallback" comment). Tilt
/// Forums is genuinely cross-manufacturer, so this type exists specifically
/// to avoid that class of silent wrong-manufacturer match: it uses the
/// manufacturer hint the master list's own section headers already provide,
/// normalized via the existing <see cref="OpdbMachineMapper.NormalizeManufacturerKey"/>,
/// to filter <see cref="IMachineRepository.QueryByTitleAsync"/>'s
/// cross-partition results down to the one partition that should contain
/// the match. A multi-match within that partition is fanned out to every
/// sibling edition (per ADR-0032, rulesheets are franchise-wide documents)
/// ONLY when <see cref="EditionFamily.IsEditionFamily"/> proves the
/// candidates are the same base game — never falling back to an unscoped
/// guess for a genuine cross-game collision.
/// </remarks>
public static class TiltForumsGameMatcher
{
    public static async Task<TiltForumsGameMatchResult> ResolveAsync(
        IMachineRepository machineRepository,
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

        if (matches.Count == 0)
        {
            return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, []);
        }

        if (matches.Count == 1)
        {
            return new TiltForumsGameMatchResult(
                TiltForumsGameMatchStatus.Resolved,
                [ToMatch(matches[0])]);
        }

        if (EditionFamily.IsEditionFamily(matches))
        {
            var siblings = await CollectSiblingsAsync(machineRepository, matches[0].GroupId!, cancellationToken);
            return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.ResolvedEditionFamily, siblings);
        }

        return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, []);
    }

    // Fetches the COMPLETE sibling set from the repository rather than
    // trusting the title-matched candidates already in hand — a sibling
    // edition can carry different exact title text (e.g. a "Collector's
    // Edition" variant), which QueryByTitleAsync would never have surfaced.
    // Matches the same primitive --sync-kineticist-tutorials already uses.
    private static async Task<IReadOnlyList<TiltForumsMachineMatch>> CollectSiblingsAsync(
        IMachineRepository machineRepository, string groupId, CancellationToken cancellationToken)
    {
        var siblings = new List<TiltForumsMachineMatch>();
        await foreach (var machine in machineRepository.GetSiblingsByGroupIdAsync(groupId, cancellationToken))
        {
            siblings.Add(ToMatch(machine));
        }
        return siblings;
    }

    private static TiltForumsMachineMatch ToMatch(Machine machine) =>
        new(machine.Id, machine.Title, machine.ManufacturerDisplayName);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TiltForumsGameMatcherTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/TiltForums/TiltForumsGameMatcher.cs tests/PinballWizard.Infrastructure.Tests/Scraping/TiltForums/TiltForumsGameMatcherTests.cs
git commit -m "feat(tiltforums) fan rulesheet matches out to sibling editions"
```

---

## Task 4: `--sync-tiltforums-rulesheets` CLI verb — loop over matches, tag EditionScope, new counter

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs:1342-1452`

**Interfaces:**
- Consumes: `TiltForumsGameMatchResult.Machines` (Task 3), `TiltForumsMachineMatch` (Task 3),
  `ChunkRequest.EditionScope` (existing optional field,
  `src/PinballWizard.Application/Rag/Chunking/Chunk.cs:39-62`).

- [ ] **Step 1: Confirm current line numbers**

Read `src/PinballWizard.Cli/Program.cs` from line 1292 to line 1455 and confirm the per-listing loop
still matches the shape described below. Line numbers may have shifted slightly since this plan was
written; use the surrounding code (the `foreach (var listing in listings)` loop, the
`matchResult.Status != ... Resolved` check, and the summary `Console.WriteLine` at the end) to
re-locate the exact boundaries if they differ.

- [ ] **Step 2: Replace the counters block**

Find:

```csharp
        var tiltForumsIndexed = 0;
        var tiltForumsSkippedNoContent = 0;
        var tiltForumsUnmatched = 0;
        var tiltForumsFailed = 0;
        var tiltForumsIndexerOptions = new PinballWizard.Application.Rag.Indexing.RagIndexerOptions();
```

Replace with:

```csharp
        var tiltForumsIndexed = 0;
        var tiltForumsSkippedNoContent = 0;
        var tiltForumsUnmatched = 0;
        var tiltForumsFailed = 0;
        var tiltForumsEditionFamilyFanouts = 0;
        var tiltForumsIndexerOptions = new PinballWizard.Application.Rag.Indexing.RagIndexerOptions();
```

- [ ] **Step 3: Replace the match-status check**

Find:

```csharp
            if (matchResult.Status != PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchStatus.Resolved)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: unmatched '{listing.GameTitle}' ({listing.ManufacturerHeaderText}) — {matchResult.Status}.");
                tiltForumsUnmatched++;
                continue;
            }
```

Replace with:

```csharp
            var isResolved = matchResult.Status is PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchStatus.Resolved
                or PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchStatus.ResolvedEditionFamily;
            if (!isResolved)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: unmatched '{listing.GameTitle}' ({listing.ManufacturerHeaderText}) — {matchResult.Status}.");
                tiltForumsUnmatched++;
                continue;
            }

            if (matchResult.Status == PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsGameMatchStatus.ResolvedEditionFamily)
            {
                tiltForumsEditionFamilyFanouts++;
            }
```

- [ ] **Step 4: Replace the fetch-and-index block to loop over `matchResult.Machines`**

Find (the block from the article fetch through the end of the per-listing loop body):

```csharp
            PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetArticle? article;
            try
            {
                article = await tiltForumsClient.FetchRulesheetAsync(listing, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: fetch failed for '{listing.GameTitle}' ({listing.TopicUrl}): {ex.Message}");
                tiltForumsFailed++;
                continue;
            }

            if (article is null)
            {
                tiltForumsSkippedNoContent++;
                continue;
            }

            string topicId;
            string documentId;
            try
            {
                topicId = new Uri(listing.TopicUrl).Segments[^1].TrimEnd('/');
                documentId = $"tiltforums_{topicId}_{matchResult.MachineId}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: failed to parse topic URL for '{listing.GameTitle}' ({listing.TopicUrl}): {ex.Message}");
                tiltForumsFailed++;
                continue;
            }

            var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                MachineId: matchResult.MachineId!,
                MachineTitle: matchResult.MachineTitle!,
                Manufacturer: matchResult.ManufacturerDisplayName!,
                DocumentId: documentId,
                DocumentUrl: article.TopicUrl,
                DocumentType: PinballWizard.Core.Models.DocumentType.Rulesheet,
                LastScrapedUtc: article.PublishedAt ?? DateTimeOffset.UtcNow);

            var chunks = tiltForumsSynthesizer.Synthesize(article, chunkRequest);
            if (chunks.Count == 0)
            {
                tiltForumsSkippedNoContent++;
                continue;
            }

            try
            {
                var result = await tiltForumsIndexer.UpsertAsync(chunkRequest, chunks, tiltForumsIndexerOptions, cancellationToken);
                if (result.Failures.Count > 0)
                {
                    foreach (var failure in result.Failures)
                    {
                        Console.Error.WriteLine(
                            $"  AI Search rejected chunk '{failure.ChunkId}' for '{article.GameTitle}': HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                    }
                    tiltForumsFailed++;
                }
                else
                {
                    Console.WriteLine($"  Indexed '{article.GameTitle}' -> machine {matchResult.MachineId} ({chunks.Count} chunk(s))");
                    tiltForumsIndexed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  Failed to index '{article.GameTitle}': {ex.Message}");
                tiltForumsFailed++;
            }
        }
```

Replace with:

```csharp
            PinballWizard.Infrastructure.Scraping.TiltForums.TiltForumsRulesheetArticle? article;
            try
            {
                article = await tiltForumsClient.FetchRulesheetAsync(listing, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: fetch failed for '{listing.GameTitle}' ({listing.TopicUrl}): {ex.Message}");
                tiltForumsFailed++;
                continue;
            }

            if (article is null)
            {
                tiltForumsSkippedNoContent++;
                continue;
            }

            string topicId;
            try
            {
                topicId = new Uri(listing.TopicUrl).Segments[^1].TrimEnd('/');
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"  Tilt Forums: failed to parse topic URL for '{listing.GameTitle}' ({listing.TopicUrl}): {ex.Message}");
                tiltForumsFailed++;
                continue;
            }

            // Per-rulesheet flags, mirroring --sync-kineticist-tutorials's
            // articleIndexed/articleHadContent pattern: `indexed` counts once
            // per rulesheet that landed on at least one machine, not once per
            // sibling edition, so the two twin verbs' summary counters mean
            // the same thing.
            var rulesheetIndexed = false;
            var rulesheetHadContent = false;

            foreach (var machineMatch in matchResult.Machines)
            {
                var documentId = $"tiltforums_{topicId}_{machineMatch.MachineId}";

                // Rulesheets describe gameplay rules, which are edition-agnostic
                // (ADR-0032) — every chunk gets the franchise-wide tag regardless
                // of whether this listing resolved to one machine or fanned out
                // to several sibling editions.
                var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                    MachineId: machineMatch.MachineId,
                    MachineTitle: machineMatch.MachineTitle,
                    Manufacturer: machineMatch.ManufacturerDisplayName,
                    DocumentId: documentId,
                    DocumentUrl: article.TopicUrl,
                    DocumentType: PinballWizard.Core.Models.DocumentType.Rulesheet,
                    LastScrapedUtc: article.PublishedAt ?? DateTimeOffset.UtcNow,
                    EditionScope: "franchise-wide");

                var chunks = tiltForumsSynthesizer.Synthesize(article, chunkRequest);
                if (chunks.Count == 0)
                {
                    continue;
                }
                rulesheetHadContent = true;

                try
                {
                    var result = await tiltForumsIndexer.UpsertAsync(chunkRequest, chunks, tiltForumsIndexerOptions, cancellationToken);
                    if (result.Failures.Count > 0)
                    {
                        foreach (var failure in result.Failures)
                        {
                            Console.Error.WriteLine(
                                $"  AI Search rejected chunk '{failure.ChunkId}' for '{article.GameTitle}': HTTP {failure.StatusCode} — {failure.ErrorMessage}");
                        }
                        tiltForumsFailed++;
                    }
                    else
                    {
                        Console.WriteLine($"  Indexed '{article.GameTitle}' -> machine {machineMatch.MachineId} ({chunks.Count} chunk(s))");
                        rulesheetIndexed = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"  Failed to index '{article.GameTitle}' -> machine {machineMatch.MachineId}: {ex.Message}");
                    tiltForumsFailed++;
                }
            }

            if (rulesheetIndexed)
            {
                tiltForumsIndexed++;
            }
            else if (!rulesheetHadContent)
            {
                tiltForumsSkippedNoContent++;
            }
        }
```

- [ ] **Step 5: Replace the summary line**

Find:

```csharp
        Console.WriteLine();
        Console.WriteLine(
            $"--sync-tiltforums-rulesheets complete: indexed={tiltForumsIndexed} unmatched={tiltForumsUnmatched} skipped_no_content={tiltForumsSkippedNoContent} failed={tiltForumsFailed}");
```

Replace with:

```csharp
        Console.WriteLine();
        Console.WriteLine(
            $"--sync-tiltforums-rulesheets complete: indexed={tiltForumsIndexed} unmatched={tiltForumsUnmatched} edition_family_fanouts={tiltForumsEditionFamilyFanouts} skipped_no_content={tiltForumsSkippedNoContent} failed={tiltForumsFailed}");
```

- [ ] **Step 6: Build**

Run: `dotnet build src/PinballWizard.Cli/PinballWizard.Cli.csproj`
Expected: Build succeeds with no errors (this verb has no dedicated CLI-level test — see the design
spec's Testing section for why; a clean build plus Task 3's matcher tests are the verification for
this task).

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git commit -m "feat(tiltforums) index every fanned-out sibling edition, tag franchise-wide scope"
```

---

## Task 5: `--sync-kineticist-tutorials` — tag `EditionScope: "franchise-wide"`

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs:1233-1240`

**Interfaces:**
- Consumes: `ChunkRequest.EditionScope` (existing optional field, unchanged signature).

No new tests — this is a one-field addition to an already-covered, already-shipped code path (the
matcher/synthesizer/indexer this verb calls have their own existing test suites, untouched by this
change).

- [ ] **Step 1: Confirm current line numbers**

Read `src/PinballWizard.Cli/Program.cs` around line 1225-1245 and confirm the `ChunkRequest`
construction inside the Kineticist per-edition loop still matches the shape below. Re-locate by
searching for `DocumentType: PinballWizard.Core.Models.DocumentType.Rulesheet` within the
`--sync-kineticist-tutorials` verb's loop if the line numbers have shifted.

- [ ] **Step 2: Add the `EditionScope` argument**

Find:

```csharp
                var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                    MachineId: machineId,
                    MachineTitle: machineTitle,
                    Manufacturer: machineManufacturer,
                    DocumentId: documentId,
                    DocumentUrl: article.CanonicalUrl,
                    DocumentType: PinballWizard.Core.Models.DocumentType.Rulesheet,
                    LastScrapedUtc: article.PublishedAt ?? DateTimeOffset.UtcNow);
```

Replace with:

```csharp
                // Kineticist tutorials are gameplay rulesheets — edition-agnostic
                // per ADR-0032 — regardless of whether this article resolved to
                // one machine or fanned out to every sibling edition.
                var chunkRequest = new PinballWizard.Application.Rag.Chunking.ChunkRequest(
                    MachineId: machineId,
                    MachineTitle: machineTitle,
                    Manufacturer: machineManufacturer,
                    DocumentId: documentId,
                    DocumentUrl: article.CanonicalUrl,
                    DocumentType: PinballWizard.Core.Models.DocumentType.Rulesheet,
                    LastScrapedUtc: article.PublishedAt ?? DateTimeOffset.UtcNow,
                    EditionScope: "franchise-wide");
```

- [ ] **Step 3: Build**

Run: `dotnet build src/PinballWizard.Cli/PinballWizard.Cli.csproj`
Expected: Build succeeds with no errors.

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git commit -m "feat(kineticist) tag rulesheet chunks with franchise-wide edition scope"
```

---

## Task 6: Full-suite verification

**Files:** None (verification only).

- [ ] **Step 1: Run the full solution test suite (excluding the categories this repo always excludes locally)**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: PASS — no regressions in `EditionFamilyTests` (Task 1), `DocumentLinkerTests` (Task 2),
`TiltForumsGameMatcherTests` (Task 3), or any other suite touched transitively by the `Machine`/
`ChunkRequest` types.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build PinballWizard.slnx`
Expected: Build succeeds with no errors or new warnings.

No commit for this task — it is a verification checkpoint only.
