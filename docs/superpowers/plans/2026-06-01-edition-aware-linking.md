# Edition-Aware Reconciliation + Linking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make document→machine linking edition-aware so each Stern per-edition document links to its correct OPDB base machine, restoring broad slug coverage and killing the Sega/Stern-class mislabel at the source.

**Architecture:** Three units. (1) The reconciler writes a game's slug to *all* OPDB base machines that share a `GroupId` (the edition family) instead of dropping multi-matches as ambiguous, and strips title decorations before matching. (2) A new `--download-documents` CLI step downloads PDFs politely so the linker's page-text tiers work. (3) The linker resolves a candidate set (multiple bases sharing a group) to the edition-correct base using the document's filename edition token plus authoritative page-1 text, fanning group-level docs out to all bases.

**Tech Stack:** .NET 10, C#, xUnit + NSubstitute, Cosmos data-plane SDK, AngleSharp/PdfPig (existing). Branch `fix/AB-259-linker-slug-population`.

**Spec:** `docs/superpowers/specs/2026-06-01-edition-aware-linking-design.md`

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/PinballWizard.Application/Sync/ScraperReconciliationService.cs` | reconcile game→machine, populate slugs | Modify: decoration-strip normalize; group-aware multi-match; `MatchOutcome.Group` |
| `src/PinballWizard.Application/Sync/ScraperReconciliationResult.cs` | reconcile result counts | Modify: add `MatchedByGroup` |
| `src/PinballWizard.Application/Linking/EditionResolver.cs` | resolve a candidate set + document → one edition's base machine (or group fan-out) | **Create** (pure, testable unit) |
| `src/PinballWizard.Application/Linking/DocumentLinker.cs` | 5-tier linker | Modify: call `EditionResolver` when slug/page resolves a same-group candidate set |
| `src/PinballWizard.Application/Downloading/DocumentDownloadService.cs` | iterate raw docs, download missing PDFs politely | **Create** |
| `src/PinballWizard.Cli/Program.cs` | CLI entry | Modify: wire `--download-documents` |
| `tests/PinballWizard.Application.Tests/Sync/ScraperReconciliationServiceTests.cs` | reconciler tests | Modify: add group + decoration tests |
| `tests/PinballWizard.Application.Tests/Linking/EditionResolverTests.cs` | edition resolver tests | **Create** |
| `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs` | linker tests | Modify: add edition end-to-end + group fan-out tests |
| `tests/PinballWizard.Application.Tests/Downloading/DocumentDownloadServiceTests.cs` | downloader tests | **Create** |
| `docs/adr/0031-document-machine-linking-source-of-truth.md` | ADR | Modify: amend decision #2 |

---

## Task 1: Decoration-stripped title normalization (reconciler)

**Files:**
- Modify: `src/PinballWizard.Application/Sync/ScraperReconciliationService.cs` (`NormalizeTitle`, lines ~208-217)
- Test: `tests/PinballWizard.Application.Tests/Sync/ScraperReconciliationServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `ScraperReconciliationServiceTests.cs`:

```csharp
[Fact]
public async Task DecoratedScrapedTitle_MatchesUndecoratedCatalogTitle()
{
    // CGC scrapes "Cactus Canyon Remake"; OPDB catalog title is "Cactus Canyon".
    var existing = MakeMachine("OPDB-CC", "cgc", "Cactus Canyon");
    StubPartition("cgc", existing);

    var catalog = CatalogOf(new GameRecord
    {
        GameId = "game_cgc_cactus-canyon",
        Title = "Cactus Canyon Remake",
        Slug = "cactus-canyon",
        GamePageUrl = "https://chicago-gaming.com/coinop/cactus-canyon/",
    });

    var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

    Assert.Equal(1, result.MatchedByTitle);
    Assert.Equal("cactus-canyon", existing.ManufacturerSlugs["cgc"]);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~DecoratedScrapedTitle"`
Expected: FAIL — `MatchedByTitle` is 0 ("Cactus Canyon Remake" normalizes to `cactuscanyonremake` ≠ `cactuscanyon`).

- [ ] **Step 3: Add decoration stripping to `NormalizeTitle`**

Replace the body of `NormalizeTitle` (around line 208) with a version that removes known decoration words before stripping to alphanumerics:

```csharp
private static readonly string[] DecorationWords =
{
    "remake", "pinball", "gamekit", "deposit", "limitededition",
    "merlinedition", "vaultedition", "edition", "standardedition",
};

/// <summary>
/// Lowercase + strip non-alphanumeric, then remove known edition/format
/// decoration tokens that manufacturer pages append but OPDB titles omit
/// ("Cactus Canyon Remake" → "cactuscanyon"). Strict enough that real
/// distinct titles never collide, loose enough that decoration drift
/// doesn't break matching.
/// </summary>
public static string NormalizeTitle(string? title)
{
    if (string.IsNullOrWhiteSpace(title)) return string.Empty;
    var sb = new System.Text.StringBuilder(title.Length);
    foreach (var c in title)
    {
        if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
    }
    var normalized = sb.ToString();
    foreach (var decoration in DecorationWords)
    {
        if (normalized.Length > decoration.Length && normalized.EndsWith(decoration, StringComparison.Ordinal))
        {
            normalized = normalized[..^decoration.Length];
        }
    }
    return normalized;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~DecoratedScrapedTitle"`
Expected: PASS.

- [ ] **Step 5: Run the full reconciler suite (no regressions)**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ScraperReconciliationServiceTests"`
Expected: all PASS (existing slug/title/ambiguous/unmatched tests still green — decoration-strip only triggers on trailing decoration tokens).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Sync/ScraperReconciliationService.cs tests/PinballWizard.Application.Tests/Sync/ScraperReconciliationServiceTests.cs
git commit -m "fix(linking) AB#259: decoration-stripped title match in reconciler"
```

---

## Task 2: Group-aware multi-match (reconciler writes slug to all bases sharing a GroupId)

**Files:**
- Modify: `src/PinballWizard.Application/Sync/ScraperReconciliationResult.cs` (add `MatchedByGroup`)
- Modify: `src/PinballWizard.Application/Sync/ScraperReconciliationService.cs` (`ReconcileAsync`, `FindMatch`, `MatchOutcome`)
- Test: `tests/PinballWizard.Application.Tests/Sync/ScraperReconciliationServiceTests.cs`

- [ ] **Step 1: Write the failing test (group case → slug on BOTH bases)**

```csharp
[Fact]
public async Task SameGroupTitleCollision_WritesSlugToAllBasesInGroup()
{
    // Two Stern Godzilla base machines sharing GroupId "GweeP":
    // Pro (GweeP-MW95j) and Premium/LE (GweeP-Ml9pZ). One scraped "Godzilla"
    // page → slug written to BOTH (the edition family), NOT dropped as ambiguous.
    var pro = MakeMachine("GweeP-MW95j", "stern", "Godzilla (Pro)");
    pro.GroupId = "GweeP";
    var premLe = MakeMachine("GweeP-Ml9pZ", "stern", "Godzilla (Premium/LE)");
    premLe.GroupId = "GweeP";
    StubPartition("stern", pro, premLe);

    var catalog = CatalogOf(new GameRecord
    {
        GameId = "game_godzilla",
        Title = "Godzilla",
        Slug = "godzilla",
        GamePageUrl = "https://sternpinball.com/game/godzilla/",
    });

    var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

    Assert.Equal(1, result.MatchedByGroup);
    Assert.Equal(0, result.AmbiguousTitle);
    Assert.Equal(2, result.Upserts);
    Assert.Equal("godzilla", pro.ManufacturerSlugs["stern"]);
    Assert.Equal("godzilla", premLe.ManufacturerSlugs["stern"]);
}
```

- [ ] **Step 2: Write the failing test (true collision → STILL ambiguous)**

The existing `AmbiguousTitle_LogsAndSkips` (Star Trek 1979 vs 2013) must keep passing — those two have **no** `GroupId` set, so they are genuinely unrelated. Add an explicit assertion that a cross-group collision (different GroupIds) stays ambiguous:

```csharp
[Fact]
public async Task DifferentGroupTitleCollision_StaysAmbiguous()
{
    // Two machines, same normalized title, DIFFERENT groups → genuinely
    // unrelated (not an edition family) → ambiguous, no slug written.
    var a = MakeMachine("AAAA-1", "stern", "Star Trek");
    a.GroupId = "AAAA";
    var b = MakeMachine("BBBB-1", "stern", "Star Trek");
    b.GroupId = "BBBB";
    StubPartition("stern", a, b);

    var catalog = CatalogOf(new GameRecord
    {
        GameId = "game_star-trek", Title = "Star Trek", Slug = "star-trek",
        GamePageUrl = "https://sternpinball.com/game/star-trek/",
    });

    var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

    Assert.Equal(1, result.AmbiguousTitle);
    Assert.Equal(0, result.MatchedByGroup);
    Assert.Equal(0, result.Upserts);
    Assert.Empty(a.ManufacturerSlugs);
    Assert.Empty(b.ManufacturerSlugs);
}
```

- [ ] **Step 3: Run both tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~GroupTitleCollision"`
Expected: FAIL — `MatchedByGroup` doesn't exist yet (compile error) / current code returns Ambiguous for the group case.

- [ ] **Step 4: Add `MatchedByGroup` to the result record**

In `ScraperReconciliationResult.cs`, add the property alongside the existing counts:

```csharp
/// <summary>Records whose title matched multiple base machines sharing a
/// GroupId (an edition family); the slug was written to every base.</summary>
public int MatchedByGroup { get; init; }
```

- [ ] **Step 5: Change `FindMatch` to return the full match set and a Group outcome**

Replace the `MatchOutcome` enum and `FindMatch` so a multi-match that shares a single `GroupId` returns `Group` with all machines, while a multi-match across different groups stays `Ambiguous`:

```csharp
private enum MatchOutcome { None, Slug, Title, Group, Ambiguous }

// Returns the matched machine(s). Single → (one, Slug|Title).
// Multiple sharing one GroupId → (all, Group). Multiple across groups → (empty, Ambiguous).
private (List<Machine> Machines, MatchOutcome Via) FindMatch(
    List<Machine> partition, string manufacturer, GameRecord game)
{
    // Pass 1: slug fast path (single machine).
    foreach (var machine in partition)
    {
        if (machine.ManufacturerSlugs.TryGetValue(manufacturer, out var existingSlug)
            && string.Equals(existingSlug, game.Slug, StringComparison.OrdinalIgnoreCase))
        {
            return ([machine], MatchOutcome.Slug);
        }
    }

    // Pass 2: title-normalize.
    var normalizedScraped = NormalizeTitle(game.Title);
    if (normalizedScraped.Length == 0) return ([], MatchOutcome.None);

    var matches = partition
        .Where(m => NormalizeTitle(m.Title) == normalizedScraped)
        .ToList();

    if (matches.Count == 0) return ([], MatchOutcome.None);
    if (matches.Count == 1) return (matches, MatchOutcome.Title);

    // Multiple matches: an edition family iff they all share one non-null GroupId.
    var groups = matches.Select(m => m.GroupId).Distinct().ToList();
    if (groups.Count == 1 && groups[0] is not null)
    {
        return (matches, MatchOutcome.Group);
    }

    _logger.LogWarning(
        "Reconciler: scraped {GameId} ('{Title}') matches multiple Machines across different groups; manual triage required. Candidates: {Candidates}",
        game.GameId, game.Title, string.Join(", ", matches.Select(m => $"{m.Id}(group={m.GroupId ?? "null"})")));
    return ([], MatchOutcome.Ambiguous);
}
```

- [ ] **Step 6: Update `ReconcileAsync` to apply across all matched machines + count Group**

Replace the match-handling block in `ReconcileAsync` (lines ~73-97):

```csharp
var (matches, matchedVia) = FindMatch(partition, manufacturer, game);

if (matches.Count == 0)
{
    if (matchedVia == MatchOutcome.Ambiguous) ambiguous++;
    else
    {
        _logger.LogWarning(
            "Reconciler: no Machine matched scraped {GameId} (slug='{Slug}', title='{Title}', manufacturer='{Manufacturer}'). OPDB may not have this machine yet.",
            game.GameId, game.Slug, game.Title, manufacturer);
        unmatched++;
    }
    continue;
}

foreach (var match in matches)
{
    ApplyScraperFields(match, game, manufacturer, now);
    await _repository.UpsertAsync(match, cancellationToken).ConfigureAwait(false);
    upserts++;
}

switch (matchedVia)
{
    case MatchOutcome.Slug: matchedBySlug++; break;
    case MatchOutcome.Title: matchedByTitle++; break;
    case MatchOutcome.Group: matchedByGroup++; break;
}
```

Add `var matchedByGroup = 0;` with the other counters (~line 45), include it in the completion log and the returned `ScraperReconciliationResult { …, MatchedByGroup = matchedByGroup }`.

- [ ] **Step 7: Run the new tests + full reconciler suite**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~ScraperReconciliationServiceTests"`
Expected: all PASS — new group test green, `AmbiguousTitle_LogsAndSkips` still green (Star Trek 1979/2013 have null GroupIds → `groups.Count==1 && groups[0] is null` is false → Ambiguous), `DifferentGroupTitleCollision` green.

- [ ] **Step 8: Commit**

```bash
git add src/PinballWizard.Application/Sync/ tests/PinballWizard.Application.Tests/Sync/
git commit -m "fix(linking) AB#259: group-aware reconcile — slug to all bases in an edition family"
```

---

## Task 3: EditionResolver — filename token → edition

**Files:**
- Create: `src/PinballWizard.Application/Linking/EditionResolver.cs`
- Create: `tests/PinballWizard.Application.Tests/Linking/EditionResolverTests.cs`

- [ ] **Step 1: Write the failing test (filename token extraction)**

Create `EditionResolverTests.cs`:

```csharp
using PinballWizard.Application.Linking;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

public sealed class EditionResolverTests
{
    [Theory]
    [InlineData("Godzilla_Pro_web.pdf", "pro")]
    [InlineData("GODZILLA-PRO-New-Address-compressed.pdf", "pro")]
    [InlineData("Godzilla_LE_Pre_web.pdf", "le")]
    [InlineData("GODZILLA-PREM-New-Address-compressed.pdf", "premium")]
    [InlineData("Godzilla_70th_web.pdf", "70th")]
    public void ExtractEditionToken_FromFilename(string filename, string expected)
    {
        Assert.Equal(expected, EditionResolver.ExtractEditionToken(filename));
    }

    [Theory]
    [InlineData("Godzilla-Pinball-Feature-Matrix-3kjhasdf.pdf")]
    [InlineData("Godzilla-Rulesheet.pdf")]
    public void ExtractEditionToken_GroupLevelDoc_ReturnsNull(string filename)
    {
        Assert.Null(EditionResolver.ExtractEditionToken(filename));
    }

    [Theory]
    [InlineData("Godzilla-Pinball-Feature-Matrix-3kjhasdf.pdf", true)]
    [InlineData("Godzilla-Rulesheet.pdf", true)]
    [InlineData("Godzilla_Pro_web.pdf", false)]
    public void IsGroupLevelDoc(string filename, bool expected)
    {
        Assert.Equal(expected, EditionResolver.IsGroupLevelDoc(filename));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~EditionResolverTests"`
Expected: FAIL — `EditionResolver` does not exist.

- [ ] **Step 3: Create `EditionResolver` with the static token helpers**

```csharp
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Linking;

/// <summary>
/// Resolves a candidate set of OPDB base machines that share a GroupId (an
/// edition family) plus a document to the single edition-correct base machine,
/// using the document's filename edition token and (when available) page-1
/// text. Group-level documents (feature matrix, rulesheet) fan out to every
/// base in the group. Pure / no I/O — the linker supplies the page text.
/// </summary>
public static class EditionResolver
{
    // Ordered: most specific token first so "_le_pre_" matches "le" before "premium".
    private static readonly (string Marker, string Token)[] FilenameMarkers =
    {
        ("70th", "70th"),
        ("_pro_", "pro"), ("-pro-", "pro"),
        ("_le_", "le"), ("-le-", "le"),
        ("_prem", "premium"), ("-prem", "premium"), ("premium", "premium"),
    };

    private static readonly string[] GroupLevelMarkers =
    {
        "feature-matrix", "featurematrix", "rulesheet", "rule-sheet",
    };

    /// <summary>Returns a normalized edition token from a filename, or null if none / group-level.</summary>
    public static string? ExtractEditionToken(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        var lower = filename.ToLowerInvariant();
        if (IsGroupLevelDoc(lower)) return null;
        foreach (var (marker, token) in FilenameMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal)) return token;
        }
        return null;
    }

    /// <summary>True when the filename signals an all-editions document.</summary>
    public static bool IsGroupLevelDoc(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        var lower = filename.ToLowerInvariant();
        return GroupLevelMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~EditionResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Linking/EditionResolver.cs tests/PinballWizard.Application.Tests/Linking/EditionResolverTests.cs
git commit -m "feat(linking) AB#259: EditionResolver filename token + group-doc detection"
```

---

## Task 4: EditionResolver — match a token to the edition-correct base machine

**Files:**
- Modify: `src/PinballWizard.Application/Linking/EditionResolver.cs`
- Modify: `tests/PinballWizard.Application.Tests/Linking/EditionResolverTests.cs`

- [ ] **Step 1: Write the failing test (token → base machine)**

Add to `EditionResolverTests.cs`:

```csharp
private static Machine Base(string id, string group, string title) => new()
{
    Id = id, PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball",
    Title = title, GroupId = group,
};

[Fact]
public void Resolve_ProToken_PicksProBase()
{
    var pro = Base("GweeP-MW95j", "GweeP", "Godzilla (Pro)");
    var premLe = Base("GweeP-Ml9pZ", "GweeP", "Godzilla (Premium/LE)");

    var result = EditionResolver.Resolve("Godzilla_Pro_web.pdf", page1Text: null, [pro, premLe]);

    Assert.False(result.IsGroupFanOut);
    Assert.Single(result.Machines);
    Assert.Equal("GweeP-MW95j", result.Machines[0].Id);
}

[Fact]
public void Resolve_LeToken_PicksPremiumLeBase()
{
    var pro = Base("GweeP-MW95j", "GweeP", "Godzilla (Pro)");
    var premLe = Base("GweeP-Ml9pZ", "GweeP", "Godzilla (Premium/LE)");

    var result = EditionResolver.Resolve("Godzilla_LE_Pre_web.pdf", page1Text: null, [pro, premLe]);

    Assert.Single(result.Machines);
    Assert.Equal("GweeP-Ml9pZ", result.Machines[0].Id);
}

[Fact]
public void Resolve_GroupLevelDoc_FansOutToAllBases()
{
    var pro = Base("GweeP-MW95j", "GweeP", "Godzilla (Pro)");
    var premLe = Base("GweeP-Ml9pZ", "GweeP", "Godzilla (Premium/LE)");

    var result = EditionResolver.Resolve("Godzilla-Rulesheet.pdf", page1Text: null, [pro, premLe]);

    Assert.True(result.IsGroupFanOut);
    Assert.Equal(2, result.Machines.Count);
}

[Fact]
public void Resolve_Page1OverridesMisleadingFilename()
{
    var pro = Base("GweeP-MW95j", "GweeP", "Godzilla (Pro)");
    var premLe = Base("GweeP-Ml9pZ", "GweeP", "Godzilla (Premium/LE)");

    // Filename says LE, but the PDF page 1 says PRO MANUAL — page 1 wins.
    var result = EditionResolver.Resolve(
        "Godzilla_LE_mislabeled.pdf",
        page1Text: "GODZILLA PRO MANUAL 500-55T5-01 TABLE OF CONTENTS",
        [pro, premLe]);

    Assert.Single(result.Machines);
    Assert.Equal("GweeP-MW95j", result.Machines[0].Id);
}

[Fact]
public void Resolve_NoEditionSignal_ReturnsUnresolved()
{
    var pro = Base("GweeP-MW95j", "GweeP", "Godzilla (Pro)");
    var premLe = Base("GweeP-Ml9pZ", "GweeP", "Godzilla (Premium/LE)");

    var result = EditionResolver.Resolve("Godzilla_mystery.pdf", page1Text: null, [pro, premLe]);

    Assert.True(result.IsUnresolved);
    Assert.Empty(result.Machines);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~EditionResolverTests"`
Expected: FAIL — `Resolve` and the result type don't exist.

- [ ] **Step 3: Add the `Resolve` method + result type**

Append to `EditionResolver.cs`:

```csharp
/// <summary>Outcome of resolving a candidate set against a document.</summary>
public sealed record EditionResolution(
    IReadOnlyList<Machine> Machines, bool IsGroupFanOut, bool IsUnresolved)
{
    public static EditionResolution Single(Machine m) => new([m], false, false);
    public static EditionResolution FanOut(IReadOnlyList<Machine> all) => new(all, true, false);
    public static EditionResolution Unresolved() => new([], false, true);
}

// Token → the marker words expected in a candidate Title.
private static readonly Dictionary<string, string[]> TokenTitleMarkers = new()
{
    ["pro"]     = ["pro"],
    ["le"]      = ["premium/le", "le", "premium"],
    ["premium"] = ["premium/le", "premium", "le"],
    ["70th"]    = ["70th", "anniversary"],
};

/// <summary>
/// Resolve a document to the edition-correct base machine in a same-group
/// candidate set. Page-1 text (when present) is authoritative and overrides
/// the filename token; group-level docs fan out to all bases; no edition
/// signal at all → unresolved (caller leaves NotInCatalog for admin review).
/// </summary>
public static EditionResolution Resolve(
    string filename, string? page1Text, IReadOnlyList<Machine> candidates)
{
    if (candidates.Count == 0) return EditionResolution.Unresolved();
    if (candidates.Count == 1) return EditionResolution.Single(candidates[0]);
    if (IsGroupLevelDoc(filename)) return EditionResolution.FanOut(candidates);

    // Page-1 text is authoritative; fall back to filename token.
    var token = ExtractEditionFromPageText(page1Text) ?? ExtractEditionToken(filename);
    if (token is null) return EditionResolution.Unresolved();

    if (!TokenTitleMarkers.TryGetValue(token, out var markers)) return EditionResolution.Unresolved();

    var match = candidates.FirstOrDefault(m =>
        markers.Any(marker => m.Title.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    return match is not null ? EditionResolution.Single(match) : EditionResolution.Unresolved();
}

private static string? ExtractEditionFromPageText(string? page1Text)
{
    if (string.IsNullOrEmpty(page1Text)) return null;
    var lower = page1Text.ToLowerInvariant();
    if (lower.Contains("pro manual", StringComparison.Ordinal)) return "pro";
    if (lower.Contains("le manual", StringComparison.Ordinal)
        || lower.Contains("premium manual", StringComparison.Ordinal)) return "le";
    if (lower.Contains("70th", StringComparison.Ordinal)) return "70th";
    return null;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~EditionResolverTests"`
Expected: PASS (all 11 cases — token extraction + the 5 resolve cases).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Linking/EditionResolver.cs tests/PinballWizard.Application.Tests/Linking/EditionResolverTests.cs
git commit -m "feat(linking) AB#259: EditionResolver resolves candidate set to edition-correct base"
```

---

## Task 5: Wire EditionResolver into DocumentLinker (Tier 2 + page tiers)

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs` (`TryTier2FilenameSlug` ~510-577, `TryMatchPage` ~606-656)
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs`

- [ ] **Step 1: Write the failing end-to-end test (Pro doc → Pro machine via Tier 2)**

Add to `DocumentLinkerTests.cs` (follow the file's existing fixture builders — read the top of the file for `MakeRawDoc` / machine-repo stubbing helpers and mirror them). The behavior to assert:

```csharp
[Fact]
public async Task FilenameTier_GodzillaProDoc_LinksToProBase()
{
    // Two Stern Godzilla bases share slug "godzilla" (group GweeP); the Pro
    // manual must land on GweeP-MW95j, not the Premium/LE base.
    var pro = MakeMachine("GweeP-MW95j", "stern", "Godzilla (Pro)");
    pro.GroupId = "GweeP"; pro.ManufacturerSlugs["stern"] = "godzilla";
    var premLe = MakeMachine("GweeP-Ml9pZ", "stern", "Godzilla (Premium/LE)");
    premLe.GroupId = "GweeP"; premLe.ManufacturerSlugs["stern"] = "godzilla";
    StubMachines(pro, premLe);

    var raw = MakeRawDoc(
        documentId: "doc_gz_pro",
        fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf",
        sourceType: SourceType.ManualsPage);
    StubRawDocs(raw);

    var linker = BuildLinker();
    await linker.InitializeAsync(CancellationToken.None);
    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.Linked, result.FinalStatus);
    Assert.Equal(["GweeP-MW95j"], result.LinkedMachineIds);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~GodzillaProDoc_LinksToProBase"`
Expected: FAIL — Tier 2 currently returns the slug-collision set through `PreferByManufacturer`, which returns null on a same-manufacturer (both Stern) tie → NotInCatalog.

- [ ] **Step 3: Insert edition resolution into Tier 2 before the ambiguity verdict**

In `TryTier2FilenameSlug`, after `bestMatches` is built and before the `PreferByManufacturer`/`ambiguous` block (around line 544), add: when `bestMatches` has >1 machine all sharing one `GroupId`, run the EditionResolver using the filename (page text is supplied null at Tier 2; the page tiers cover page text):

```csharp
// Same-group candidate set (an edition family) → resolve by edition.
if (bestMatches.Count > 1)
{
    var groups = bestMatches.Select(m => m.GroupId).Distinct().ToList();
    if (groups.Count == 1 && groups[0] is not null)
    {
        var resolution = EditionResolver.Resolve(filename, page1Text: null, bestMatches);
        if (resolution.IsGroupFanOut)
        {
            return new LinkingResult(raw.DocumentId, LinkStatus.Linked, "filename_edition_group",
                resolution.Machines.Select(m => m.Id).ToList(), FailureReason: null);
        }
        if (!resolution.IsUnresolved)
        {
            return new LinkingResult(raw.DocumentId, LinkStatus.Linked, "filename_edition",
                [resolution.Machines[0].Id], FailureReason: null);
        }
        // Unresolved → fall through to page tiers (which add page-1 authority).
        return null;
    }
}
```

(`filename` is the local already computed at line 516. Keep the existing single-match and cross-manufacturer paths unchanged below.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~GodzillaProDoc_LinksToProBase"`
Expected: PASS.

- [ ] **Step 5: Write the page-tier test (page-1 authority + group fan-out)**

```csharp
[Fact]
public async Task PageTier_GroupRulesheet_FansOutToAllGroupBases()
{
    var pro = MakeMachine("GweeP-MW95j", "stern", "Godzilla (Pro)");
    pro.GroupId = "GweeP"; pro.ManufacturerSlugs["stern"] = "godzilla";
    var premLe = MakeMachine("GweeP-Ml9pZ", "stern", "Godzilla (Premium/LE)");
    premLe.GroupId = "GweeP"; premLe.ManufacturerSlugs["stern"] = "godzilla";
    StubMachines(pro, premLe);

    // A rulesheet whose page text matches the "godzilla" slug → group fan-out.
    var raw = MakeRawDocWithLocalFile(
        documentId: "doc_gz_rules",
        fileUrl: "https://sternpinball.com/wp-content/uploads/2022/06/Godzilla-Rulesheet.pdf",
        sourceType: SourceType.ManualsPage,
        page1Text: "GODZILLA rulesheet — applies to all editions");
    StubRawDocs(raw);

    var linker = BuildLinker();
    await linker.InitializeAsync(CancellationToken.None);
    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.Linked, result.FinalStatus);
    Assert.Equal(2, result.LinkedMachineIds.Count);
    Assert.Contains("GweeP-MW95j", result.LinkedMachineIds);
    Assert.Contains("GweeP-Ml9pZ", result.LinkedMachineIds);
}
```

- [ ] **Step 6: Run to verify it fails, then add edition resolution to `TryMatchPage`**

In `TryMatchPage`, replace the `matchedMachines.Count > 1` block (around line 635) so a same-group candidate set is edition-resolved with the page text as authority:

```csharp
if (matchedMachines.Count > 1)
{
    var groups = matchedMachines.Select(m => m.GroupId).Distinct().ToList();
    if (groups.Count == 1 && groups[0] is not null)
    {
        var filename = ExtractFilename(raw.Source.FileUrl ?? string.Empty);
        var resolution = EditionResolver.Resolve(filename, extracted.Pages[pageIndex].Text, matchedMachines);
        if (resolution.IsGroupFanOut)
            matchedMachines = resolution.Machines.ToList();
        else if (!resolution.IsUnresolved)
            matchedMachines = [resolution.Machines[0]];
        // else: leave matchedMachines as-is → multi-machine fan-out (legacy behavior).
    }
    else
    {
        var preferred = PreferByManufacturer(matchedMachines, LinkingUtilities.InferManufacturerKey(raw.Source));
        if (preferred is not null) matchedMachines = [preferred];
    }
}
```

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~PageTier_GroupRulesheet"`
Expected: PASS.

- [ ] **Step 7: Run the full linker suite (no regressions)**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~DocumentLinkerTests"`
Expected: all PASS — the prior PR #314 manufacturer-disambiguation tests (Sega vs Stern across *different* partitions) still pass because that path (different PartitionKey) never enters the same-group branch.

- [ ] **Step 8: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs
git commit -m "fix(linking) AB#259: edition-aware Tier 2 + page tiers in DocumentLinker"
```

---

## Task 6: DocumentDownloadService + `--download-documents` CLI

**Files:**
- Modify: `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs` (add `StreamAllAsync`, `UpdateFileAsync`)
- Modify: the Cosmos impl of `IRawDocumentRepository` (implement the two new methods)
- Create: `src/PinballWizard.Application/Downloading/DocumentDownloadService.cs`
- Create: `tests/PinballWizard.Application.Tests/Downloading/DocumentDownloadServiceTests.cs`
- Modify: `src/PinballWizard.Cli/Program.cs`

**Confirmed interface gap** (read 2026-06-01): `IRawDocumentRepository` has `StreamByStatusAsync` + `UpdateLinkStatusAsync` but **no** stream-all or file-update. Add both (Step 0 below) before the service.

- [ ] **Step 0: Add `StreamAllAsync` + `UpdateFileAsync` to `IRawDocumentRepository` and its Cosmos impl**

In `IRawDocumentRepository.cs`:

```csharp
    // Stream every raw record (all statuses) — used by the document downloader.
    IAsyncEnumerable<RawDocumentRecord> StreamAllAsync(CancellationToken cancellationToken);

    // Persist the downloaded-file metadata on an existing record. Provenance-
    // preserving: only the File field is replaced; Source/link metadata untouched.
    Task UpdateFileAsync(string documentId, DownloadedFileInfo file, CancellationToken cancellationToken);
```

In the Cosmos implementation (`CosmosRawDocumentRepository`, alongside `StreamByStatusAsync`/`UpdateLinkStatusAsync`): `StreamAllAsync` issues `SELECT * FROM c` (no status filter); `UpdateFileAsync` point-reads the record, sets `record.File = file`, and upserts (mirror the existing `UpdateLinkStatusAsync` read-modify-write). Build to confirm it compiles before writing the service test.

- [ ] **Step 1: Write the failing test (downloads missing, skips present)**

Create `DocumentDownloadServiceTests.cs`. Mock `IFileDownloader` + `IRawDocumentRepository`; assert a raw doc with null `File` is downloaded and its `LocalPath` stamped, while a doc with an existing `File.LocalPath` is skipped:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Downloading;

public sealed class DocumentDownloadServiceTests
{
    private readonly IFileDownloader _downloader = Substitute.For<IFileDownloader>();
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();

    [Fact]
    public async Task Downloads_MissingFile_AndStampsLocalPath()
    {
        var raw = MakeRaw("doc_a", "https://sternpinball.com/x/Godzilla_Pro_web.pdf", file: null);
        StubStream(raw);
        _downloader.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult
            {
                Status = DownloadStatus.Downloaded, FileUrl = raw.Source.FileUrl!,
                LocalPath = "stern/Godzilla_Pro_web.pdf", SizeBytes = 1234, Sha256 = "abc",
            });

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance, downloadsRoot: "/tmp/dl");
        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        await _repo.Received(1).UpdateFileAsync("doc_a",
            Arg.Is<DownloadedFileInfo>(f => f.LocalPath == "stern/Godzilla_Pro_web.pdf"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_AlreadyDownloaded()
    {
        var raw = MakeRaw("doc_b", "https://sternpinball.com/x/y.pdf",
            file: new DownloadedFileInfo { LocalPath = "stern/y.pdf" });
        StubStream(raw);

        var svc = new DocumentDownloadService(_repo, _downloader, NullLogger<DocumentDownloadService>.Instance, downloadsRoot: "/tmp/dl");
        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        await _downloader.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HttpMetadata?>(), Arg.Any<CancellationToken>());
    }

    // MakeRaw / StubStream helpers: mirror DocumentLinkerTests raw-doc builders;
    // StubStream sets _repo.StreamAllAsync(...) to yield the given docs.
}
```

(If `IRawDocumentRepository` lacks `UpdateFileAsync` or a `StreamAllAsync`, read the interface and use the existing enumerator/update methods — adapt the asserted method names to what exists. Add a minimal `UpdateFileAsync` to the interface + Cosmos impl only if none exists.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~DocumentDownloadServiceTests"`
Expected: FAIL — service doesn't exist.

- [ ] **Step 3: Create `DocumentDownloadService`**

```csharp
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Downloading;

/// <summary>
/// Downloads every not-yet-downloaded document in scraped_documents_raw so the
/// linker's page-text tiers can read page-1 content. Polite (the injected
/// IFileDownloader routes through the politeness gate), idempotent (skips docs
/// that already have a local file), and bounded (the downloader's HttpClient
/// owns the read timeout).
/// </summary>
public sealed class DocumentDownloadService
{
    private readonly IRawDocumentRepository _repo;
    private readonly IFileDownloader _downloader;
    private readonly ILogger<DocumentDownloadService> _logger;
    private readonly string _downloadsRoot;

    public DocumentDownloadService(
        IRawDocumentRepository repo, IFileDownloader downloader,
        ILogger<DocumentDownloadService> logger, string downloadsRoot)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(downloadsRoot);
        _repo = repo; _downloader = downloader; _logger = logger; _downloadsRoot = downloadsRoot;
    }

    public async Task<DownloadSummary> RunAsync(CancellationToken cancellationToken)
    {
        int downloaded = 0, skipped = 0, failed = 0;

        await foreach (var raw in _repo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (raw.File?.LocalPath is not null) { skipped++; continue; }

            var fileUrl = raw.Source.FileUrl;
            if (string.IsNullOrEmpty(fileUrl)) { skipped++; continue; }

            var relPath = BuildLocalPath(raw);
            var result = await _downloader
                .DownloadAsync(fileUrl, Path.Combine(_downloadsRoot, relPath), raw.Http, cancellationToken)
                .ConfigureAwait(false);

            if (result.Status is DownloadStatus.Downloaded or DownloadStatus.NotModified)
            {
                await _repo.UpdateFileAsync(raw.DocumentId, new DownloadedFileInfo
                {
                    LocalPath = result.LocalPath, SizeBytes = result.SizeBytes ?? 0, Sha256 = result.Sha256,
                }, cancellationToken).ConfigureAwait(false);
                downloaded++;
            }
            else
            {
                _logger.LogWarning("DocumentDownload: {DocId} failed ({Status}): {Err}",
                    raw.DocumentId, result.Status, result.ErrorMessage);
                failed++;
            }
        }

        _logger.LogInformation("DocumentDownload complete: downloaded={Downloaded} skipped={Skipped} failed={Failed}",
            downloaded, skipped, failed);
        return new DownloadSummary(downloaded, skipped, failed);
    }

    private static string BuildLocalPath(RawDocumentRecord raw)
    {
        var mfr = raw.Source.SourceType.ToString().ToLowerInvariant();
        var filename = Path.GetFileName(new Uri(raw.Source.FileUrl!).AbsolutePath);
        return Path.Combine(mfr, filename);
    }
}

public sealed record DownloadSummary(int Downloaded, int Skipped, int Failed);
```

(Adapt `DownloadedFileInfo`'s exact required members and `raw.Http`/`raw.Source` property names to the real models read in Step 1. If `IRawDocumentRepository` has no `StreamAllAsync`/`UpdateFileAsync`, add them: `StreamAllAsync` mirrors `StreamByStatusAsync`; `UpdateFileAsync` is a point-replace of the `File` field. Keep the Cosmos impl change minimal and provenance-preserving.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~DocumentDownloadServiceTests"`
Expected: PASS.

- [ ] **Step 5: Wire the `--download-documents` CLI flag**

In `src/PinballWizard.Cli/Program.cs`, register an option mirroring `--link-documents` and dispatch to `DocumentDownloadService.RunAsync`, gated on Cosmos being configured (reuse the existing `cosmosWired` check). Print the summary like the other commands:

```csharp
// (option declaration alongside the others)
var downloadDocumentsOption = new Option<bool>("--download-documents",
    "Download every not-yet-downloaded document in scraped_documents_raw to the local downloads root so the linker's page-text tiers can read page-1 content. Polite + idempotent. Requires Cosmos.");

// (dispatch, alongside the --link-documents handler)
if (downloadDocuments)
{
    var svc = host.Services.GetRequiredService<DocumentDownloadService>();
    var summary = await svc.RunAsync(cts.Token);
    Console.WriteLine($"--download-documents complete: downloaded={summary.Downloaded} skipped={summary.Skipped} failed={summary.Failed}");
    return;
}
```

Register `DocumentDownloadService` in the DI block where `DocumentLinker` is registered (it needs `IRawDocumentRepository`, `IFileDownloader`, the downloads root).

- [ ] **Step 6: Build + full Application test suite**

Run: `dotnet build PinballWizard.slnx -c Release` then `dotnet test tests/PinballWizard.Application.Tests`
Expected: build 0/0; all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Downloading/ src/PinballWizard.Cli/Program.cs tests/PinballWizard.Application.Tests/Downloading/
git commit -m "feat(linking) AB#259: --download-documents revives page-text tiers (D13)"
```

---

## Task 7: Amend ADR-0031

**Files:**
- Modify: `docs/adr/0031-document-machine-linking-source-of-truth.md`

- [ ] **Step 1: Update decision #2**

Replace the "add OPDB id + edition hint to `GameRecord`" wording with the group-aware approach actually implemented: the reconciler writes the slug to all base machines sharing a `GroupId`; the linker's `EditionResolver` resolves per-document edition from filename + page-1 text against the catalog's existing `GroupId`/`Title`/`features`. Note explicitly that **no `GameRecord` schema change was needed** — the catalog already carries the group/edition structure.

- [ ] **Step 2: Commit**

```bash
git add docs/adr/0031-document-machine-linking-source-of-truth.md
git commit -m "docs(adr) AB#259: amend ADR-0031 #2 — GroupId-based edition resolution, no GameRecord change"
```

---

## Task 8: Full-suite green + run the live migration steps

**Files:** none (verification + data ops per `thoughts/shared/plans/2026-06-01_AB-259_data-pipeline-reassessment.md §4`).

- [ ] **Step 1: Full solution test suite**

Run: `dotnet test PinballWizard.slnx -c Release`
Expected: all green (the suite was 1,855 at handoff; new tests added, none removed).

- [ ] **Step 2: Build the CLI for the live ops**

Run: `dotnet build PinballWizard.slnx -c Release`
Expected: 0/0.

- [ ] **Step 3: Live Step 2 — re-reconcile (gate G1+G2)**

With live env vars (Foundry/Search/Cosmos endpoints + Opdb token), run `--source all` (which includes the reconcile) OR a reconcile-only entry if present. Verify the reconciler log: `slug-matched + title-matched + group-matched` machines ≫ 36; ambiguous cases all log both ids. Re-run `--link-documents` (built `-c Release`, via `dotnet exec`): slug index ≫ 36.

- [ ] **Step 4: Live Step 4 — download + relink (gate G4)**

`--download-documents` → `--relink-all` → `--link-documents`. Verify link-rate ≫ 0/405; spot-check via AI Search facet that the Godzilla Pro doc resolves to `GweeP-MW95j` and the LE doc to `GweeP-Ml9pZ`.

- [ ] **Step 5: Live Step 5 — rebuild index (gates G5/G6/G7)**

Clear `rag_index_state` → `--rebuild-rag-index` → `--run-rag-backfill` → `--sync-metadata-cards`. Verify every index `machine_id` ⊆ machines; Godzilla Stern chunks now under the correct Stern ids (not Sega `G5po2-MeP6B`).

- [ ] **Step 6: PAUSE for user before Step 6 (re-eval + eval-truth fix)**

Per the user's Option-A choice, stop here and report gate results before touching the eval ground-truth.

---

## Self-Review

**Spec coverage:** Unit 1 (group-aware reconciler) → Tasks 1–2. Unit 2 (downloader) → Task 6. Unit 3 (edition resolver) → Tasks 3–5. ADR amendment → Task 7. Live migration Steps 2–5 → Task 8. All spec sections covered.

**Placeholder scan:** Tasks 6's helper builders reference "mirror the existing test helpers" — this is a direct instruction to reuse named existing helpers (`MakeRawDoc`, `StubMachines`), not a placeholder; the engineer reads the existing test file. The downloader adapts to the real `IRawDocumentRepository`/`DownloadedFileInfo` shapes — flagged explicitly with the fallback (add minimal `StreamAllAsync`/`UpdateFileAsync` if absent) rather than left vague.

**Type consistency:** `EditionResolver.Resolve(filename, page1Text, candidates) → EditionResolution{Machines, IsGroupFanOut, IsUnresolved}` used identically in Tasks 4 and 5. `MatchOutcome.Group` and `MatchedByGroup` consistent across Task 2. `DownloadSummary(Downloaded, Skipped, Failed)` consistent across Task 6.

**Open adaptation point (honest):** Task 6 depends on `IRawDocumentRepository` exposing a stream-all + file-update; if those don't exist the engineer adds them minimally. This is the one spot requiring live-code adaptation — called out, not hidden.
