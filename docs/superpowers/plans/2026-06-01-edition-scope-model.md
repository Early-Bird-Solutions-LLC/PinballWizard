# Edition-Scope Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make document→machine citations edition-accurate — each document links to exactly the edition(s) it applies to (single / subset / franchise-wide), and the Wizard answers R1/R2/R3 (answer-direct-attributed-to-all-editions / answer-all-editions-attributed / honest-substitution).

**Architecture:** Add a per-base edition discriminator (`Machine.EditionTokens`) from OPDB data already on the wire; the linker matches a document's edition token against `EditionTokens` (not the franchise `Title`), which fixes over-linking at the root; thread an `EditionScope` enum through fan-out → index so every chunk self-declares its scope; the Wizard decides R1/R2/R3 from the `edition_scope` distribution of retrieved hits; the eval is reworked to reward (not penalize) edition-aware behavior.

**Tech Stack:** .NET 10, C#, xUnit + NSubstitute, Cosmos data-plane SDK, AI Search. Branch `fix/AB-259-linker-slug-population`. **Warnings-as-errors** — use `CultureInfo.InvariantCulture` for any `int.ToString()` (CA1305); never name a method after a type keyword like `Single` (CA1720, use `ForSingleEdition`).

**Spec:** `docs/superpowers/specs/2026-06-01-edition-scope-model-design.md` · **ADR:** `docs/adr/0032-document-edition-scope-model.md` · **Requirements:** `thoughts/shared/plans/2026-06-01_AB-259_edition-scope-REQUIREMENTS.md`

**Verification note:** the Cosmos data-plane SDK query plan throws `0x800A0B00` on this .NET 10 box. Verify live state via **point-reads** (`tools/probe-godzilla-slugs.csx`, `probe-godzilla-titles.csx`) or AI Search REST (admin-key curl) — NOT ad-hoc `dotnet-script` aggregate queries.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/PinballWizard.Core/Domain/Machine.cs` | machine catalog entity | Add `EditionLabel`, `EditionTokens` |
| `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineDto.cs` | OPDB wire DTO | Add `Features` |
| `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs` | OPDB→Machine map | Derive `EditionLabel`/`EditionTokens`; `MergeOpdbFieldsInto` |
| `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs` | sync orchestration | Pass-2 alias-name fold into `EditionTokens`; edition-qualified title-lookup rows |
| `src/PinballWizard.Application/Linking/EditionResolver.cs` | edition resolution | Match `EditionTokens`; `ForSubset`; full marker set; scope classifier |
| `src/PinballWizard.Application/Linking/DocumentLinker.cs` | 5-tier linker | Use scope; thread `EditionScope` to fan-out |
| `src/PinballWizard.Core/Models/RawDocument.cs` (`ScrapedDocumentRecord`) | linked-doc record | Add `EditionScope` |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapedDocumentRepository.cs` | scraped_documents writer | Write `edition_scope` |
| `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchIndexFields.cs` | index field names | Add `edition`, `edition_scope` |
| `src/PinballWizard.Infrastructure/Rag/Indexing/AiSearchIndexSchema.cs` | index schema | Add the two fields (filterable) |
| chunk pipeline (`Chunk.cs`/`ChunkRequest`, `IndexedChunkDocument`, ingestion) | scope→chunk | Thread `edition`/`editionScope` |
| `src/PinballWizard.Web` Wizard prompt (`Wizard.md`) + grounding/search DTOs | Wizard reasoning | R1/R2/R3 rules; surface edition fields |
| `src/PinballWizard.Application/Ai/Evaluation/EvalQuestion.cs` + evaluators | eval | Edition-aware schema + evaluators |
| `data/eval/wizard.v1.jsonl` | eval data | Rewrite Godzilla rows |
| `docs/adr/0031-...md` | prior ADR | Note correction (decision #3) |

**Scope note:** Tasks 1–3 (catalog + linker) are the load-bearing root-cause fix and are independently testable. Tasks 4–6 (index + Wizard + eval) build on them. Service bulletins are explicitly a **follow-up ticket** (decision #4), not in this plan.

---

## Task 1: Catalog — `Machine.EditionLabel` + `EditionTokens` from OPDB

**Files:**
- Modify: `src/PinballWizard.Core/Domain/Machine.cs` (after `Editions`, ~line 67)
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineDto.cs` (add `Features`, ~after line 79)
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs` (`Map` ~line 41-62; add `ExtractEditionLabel`/`DeriveEditionTokens` helpers)
- Test: `tests/PinballWizard.Infrastructure.Tests/Integrations/Opdb/OpdbMachineMapperTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `OpdbMachineMapperTests.cs` (mirror its existing `Map` test fixtures — read the file top for the DTO builder pattern):

```csharp
[Fact]
public void Map_EditionQualifiedName_DerivesEditionLabelAndTokens_KeepsTitleClean()
{
    var dto = new OpdbMachineDto
    {
        OpdbId = "GweeP-MW95j",
        IsMachine = true,
        Name = "Godzilla (Pro)",
        CommonName = null,
        Manufacturer = new OpdbManufacturerDto { Name = "Stern Pinball" },
        ManufactureDate = "2021-09-14",
    };

    var m = OpdbMachineMapper.Map(dto, DateTimeOffset.UtcNow, groupTitle: "Godzilla");

    Assert.NotNull(m);
    Assert.Equal("Godzilla", m!.Title);              // Title stays the franchise (ADR-0029)
    Assert.Equal("Pro", m.EditionLabel);
    Assert.Equal(["pro"], m.EditionTokens);
}

[Fact]
public void Map_NoParenthetical_EditionLabelNull()
{
    var dto = new OpdbMachineDto
    {
        OpdbId = "GJ2o0-MrRye", IsMachine = true, Name = "Toy Story 4", CommonName = "Toy Story 4",
        Manufacturer = new OpdbManufacturerDto { Name = "Jersey Jack Pinball" }, ManufactureDate = "2023-01-01",
    };
    var m = OpdbMachineMapper.Map(dto, DateTimeOffset.UtcNow);
    Assert.Null(m!.EditionLabel);
    Assert.Empty(m.EditionTokens);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~Map_EditionQualifiedName"`
Expected: FAIL — `Machine.EditionLabel`/`EditionTokens` don't exist (compile error).

- [ ] **Step 3: Add the model fields**

In `Machine.cs`, after the `Editions` property (~line 67):

```csharp
/// <summary>
/// Edition-qualified OPDB label for this base when it shares a franchise
/// (GroupId) with sibling bases — e.g. "Pro", "Premium/LE". Derived from the
/// parenthetical of OPDB's edition-qualified name. Null for singleton machines.
/// NOT the Title — Title stays the clean franchise name per ADR-0029 D1.
/// </summary>
[JsonPropertyName("editionLabel")]
public string? EditionLabel { get; set; }

/// <summary>
/// Normalized edition tokens this base answers to — e.g. ["pro"] for the Pro
/// base, ["premium","le","70th"] for the Premium/LE base (folded from its
/// alias editions). The reliable per-base discriminator the linker matches a
/// document's edition token against (NOT Title). Empty for singletons.
/// </summary>
[JsonPropertyName("editionTokens")]
public List<string> EditionTokens { get; set; } = [];
```

In `OpdbMachineDto.cs`, after `Keywords` (~line 79):

```csharp
/// <summary>OPDB edition "features" (e.g. ["Pro edition"]). Secondary edition
/// signal — used only as the EditionLabel fallback when Name has no parenthetical.</summary>
[JsonPropertyName("features")]
public List<string> Features { get; init; } = [];
```

- [ ] **Step 4: Derive in the mapper**

In `OpdbMachineMapper.cs`, add to the `Map` object initializer (after `Editions = [],`):

```csharp
            EditionLabel = ExtractEditionLabel(dto.Name, dto.Features),
            EditionTokens = DeriveEditionTokens(ExtractEditionLabel(dto.Name, dto.Features)),
```

And add the helpers (near `ExtractGroupSegment`):

```csharp
/// <summary>
/// The parenthetical of an edition-qualified OPDB name: "Godzilla (Pro)" → "Pro",
/// "Godzilla (Premium/LE)" → "Premium/LE". Falls back to the joined features when
/// the name has no parenthetical. Null when neither yields an edition label.
/// </summary>
public static string? ExtractEditionLabel(string? name, IReadOnlyList<string>? features)
{
    if (!string.IsNullOrWhiteSpace(name))
    {
        var open = name.LastIndexOf('(');
        var close = name.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            var inner = name[(open + 1)..close].Trim();
            if (inner.Length > 0) return inner;
        }
    }
    if (features is { Count: > 0 })
    {
        // "Pro edition" → "Pro"; join multiple with "/".
        var labels = features
            .Select(f => f.Replace(" edition", "", StringComparison.OrdinalIgnoreCase).Trim())
            .Where(f => f.Length > 0);
        var joined = string.Join("/", labels);
        if (joined.Length > 0) return joined;
    }
    return null;
}

/// <summary>
/// Normalized lowercase tokens from an edition label: "Premium/LE" →
/// ["premium","le"], "Pro" → ["pro"], "70th Anniversary" → ["70th"]. Splits on
/// '/' and whitespace, keeps the leading word of multiword variants, drops
/// noise words. Alias-fold (OpdbSyncService pass 2) appends more tokens later.
/// </summary>
public static List<string> DeriveEditionTokens(string? editionLabel)
{
    if (string.IsNullOrWhiteSpace(editionLabel)) return [];
    var tokens = new List<string>();
    foreach (var part in editionLabel.Split(['/', ' ', ','], StringSplitOptions.RemoveEmptyEntries))
    {
        var t = part.Trim().ToLowerInvariant();
        if (t is "anniversary" or "edition" or "and") continue;
        if (t.Length == 0 || tokens.Contains(t)) continue;
        tokens.Add(t);
    }
    return tokens;
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~OpdbMachineMapperTests"`
Expected: PASS (new + existing mapper tests).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Core/Domain/Machine.cs src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineDto.cs src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs tests/PinballWizard.Infrastructure.Tests/Integrations/Opdb/OpdbMachineMapperTests.cs
git commit -m "feat(catalog) AB#259: Machine.EditionLabel + EditionTokens from OPDB name/features"
```

---

## Task 2: Fold alias edition names into `EditionTokens` (sync pass 2) + idempotent re-sync

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs` (pass-2 alias loop, ~line 270-284 — after `baseMachine.Editions.Add(edition);`)
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs` (`MergeOpdbFieldsInto` — read the method, ~line 182+, add EditionLabel/EditionTokens assignment for re-sync)
- Test: `tests/PinballWizard.Infrastructure.Tests/Integrations/Opdb/OpdbSyncServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Read `OpdbSyncServiceTests.cs` for its repository-stub + alias-buffer pattern, then add a test asserting that after sync, the Premium/LE base's `EditionTokens` contains its alias edition names. The behavior to assert (adapt to the file's fixture builders):

```csharp
[Fact]
public async Task Sync_FoldsAliasEditionNamesIntoBaseEditionTokens()
{
    // Base: Godzilla (Premium/LE) GweeP-Ml9pZ → seed tokens ["premium","le"].
    // Aliases: -ARZoY "Premium", -A9vXB "LE", -AOvNL "70th Anniversary".
    // After sync, base.EditionTokens ⊇ {"premium","le","70th"}.
    // (Use the existing test's alias DTO builders + in-memory machine repo.)
    var baseM = MakeBaseMachine("GweeP-Ml9pZ", "stern", "Godzilla", editionLabel: "Premium/LE");
    // ... stub repo to return baseM for GweeP-Ml9pZ; feed alias DTOs ...
    await _sut.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);
    Assert.Contains("70th", baseM.EditionTokens);
    Assert.Contains("premium", baseM.EditionTokens);
    Assert.Contains("le", baseM.EditionTokens);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~FoldsAliasEditionNames"`
Expected: FAIL — alias names not folded into `EditionTokens`.

- [ ] **Step 3: Fold alias names in pass 2**

In `OpdbSyncService.cs`, immediately after `baseMachine.Editions.Add(edition);` (~line 278):

```csharp
                    // Fold the alias edition's name into the base's EditionTokens
                    // so the linker can match a per-edition document (e.g. _70th_)
                    // to this base. Tokens are additive + de-duped.
                    foreach (var token in OpdbMachineMapper.DeriveEditionTokens(edition.Name))
                    {
                        if (!baseMachine.EditionTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                        {
                            baseMachine.EditionTokens.Add(token);
                        }
                    }
```

- [ ] **Step 4: Make `MergeOpdbFieldsInto` carry the fields on re-sync**

Read `MergeOpdbFieldsInto` in `OpdbMachineMapper.cs` (~line 182). It mutates an `existing` Machine from a fresh `dto`. Add (matching its assignment style):

```csharp
        existing.EditionLabel = ExtractEditionLabel(dto.Name, dto.Features);
        // Re-derive base tokens from the label; pass-2 re-appends alias tokens
        // idempotently on the same run, so reset to the label-derived set here.
        existing.EditionTokens = DeriveEditionTokens(existing.EditionLabel);
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~OpdbSyncServiceTests"`
Expected: PASS (new + existing sync tests, including idempotency).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Integrations/Opdb/ tests/PinballWizard.Infrastructure.Tests/Integrations/Opdb/OpdbSyncServiceTests.cs
git commit -m "feat(catalog) AB#259: fold alias edition names into base EditionTokens; merge on re-sync"
```

---

## Task 3: Edition-qualified title-lookup rows (sync)

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs` (`UpdateTitleLookupAsync` is called per base ~line 201-204; read it + `MachineTitleLookup` to see the row shape, then add edition-qualified entries)
- Test: `tests/PinballWizard.Infrastructure.Tests/Integrations/Opdb/OpdbSyncServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Sync_WritesEditionQualifiedTitleLookupRows()
{
    // After sync, "godzilla pro" → GweeP-MW95j and "godzilla premium" → GweeP-Ml9pZ
    // exist as title-lookup entries (in addition to the bare "godzilla" row).
    // Assert against the title-lookup repository stub used by the existing tests.
    await _sut.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);
    Assert.Equal("GweeP-MW95j", await LookupAsync("godzilla pro", "stern"));
    Assert.Equal("GweeP-Ml9pZ", await LookupAsync("godzilla premium", "stern"));
    Assert.Equal("GweeP-Ml9pZ", await LookupAsync("godzilla le", "stern"));
}
```

(`LookupAsync` queries the same `MachineTitleLookup` store the test already stubs; adapt to the existing test's lookup-assertion pattern.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~EditionQualifiedTitleLookup"`
Expected: FAIL — no `"godzilla pro"` row.

- [ ] **Step 3: Write edition-qualified lookup rows**

Read `UpdateTitleLookupAsync` + `MachineTitleLookup` to confirm the key-normalization (`NormalizeTitle`) and write method. Then, where the per-base title-lookup is written (pass after the base upsert), add: for each base with non-empty `EditionTokens`, also upsert a lookup row keyed `NormalizeTitle($"{Title} {token}")` → base id, for each token. Match the existing write call's signature:

```csharp
        // Edition-qualified lookup rows so getMachineByTitle("Godzilla Premium")
        // resolves to the correct base. Keyed off EditionTokens.
        foreach (var token in machine.EditionTokens)
        {
            await UpdateTitleLookupAsync(
                machine.Id, machine.PartitionKey,
                priorTitle: null,
                newTitle: $"{machine.Title} {token}",
                now, cancellationToken).ConfigureAwait(false);
        }
```

(If `UpdateTitleLookupAsync` dedupes by normalized key, the bare-title row and edition rows coexist. Confirm it appends machine ids to a shared row rather than overwriting — per ADR-0025 §4; the edition-qualified key is distinct from the bare key so no collision.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~OpdbSyncServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs tests/PinballWizard.Infrastructure.Tests/Integrations/Opdb/OpdbSyncServiceTests.cs
git commit -m "feat(catalog) AB#259: edition-qualified title-lookup rows (godzilla pro -> GweeP-MW95j)"
```

---

## Task 4: `EditionResolver` matches `EditionTokens`; scope classifier; fix the false tests

**Files:**
- Modify: `src/PinballWizard.Application/Linking/EditionResolver.cs` (`Resolve` line ~71-89 match target; `FilenameMarkers` 16-22; `IsGroupLevelDoc` 46-51; add `ForSubset` + `EditionScope`)
- Modify: `tests/PinballWizard.Application.Tests/Linking/EditionResolverTests.cs` (the `Base(...)` helper currently sets `Title="Godzilla (Pro)"` — **that is the false fixture**; switch to `EditionTokens`)
- Test: same file

- [ ] **Step 1: Add `EditionScope` enum + fix the test fixtures (make them fail correctly)**

In `EditionResolver.cs`, add at namespace scope:

```csharp
/// <summary>Which editions of a franchise a document applies to.</summary>
public enum EditionScope { SingleEdition, EditionSubset, FranchiseWide }
```

In `EditionResolverTests.cs`, change the `Base` helper to set `EditionTokens` instead of an edition-qualified Title (the live catalog stores `Title="Godzilla"` for both bases — the old fixture lied):

```csharp
private static Machine Base(string id, params string[] editionTokens) => new()
{
    Id = id, PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball",
    Title = "Godzilla", GroupId = "GweeP", Year = 2021,
    EditionTokens = [.. editionTokens],
};
private static readonly Machine Pro = Base("GweeP-MW95j", "pro");
private static readonly Machine PremLe = Base("GweeP-Ml9pZ", "premium", "le", "70th");
```

The existing `Resolve_*` tests now construct bases with realistic `Title="Godzilla"` + tokens.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~EditionResolverTests"`
Expected: FAIL — `Resolve` still matches `m.Title.Contains("pro")` which is now false for `Title="Godzilla"` → returns Unresolved.

- [ ] **Step 3: Match `EditionTokens`**

In `EditionResolver.Resolve`, replace the title-match (line ~85-86):

```csharp
        var match = candidates.FirstOrDefault(m =>
            m.EditionTokens.Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase)));
```

Remove the now-unused `TokenTitleMarkers` dictionary if nothing else references it.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~EditionResolverTests"`
Expected: PASS — `Godzilla_Pro_web.pdf` → `GweeP-MW95j` (token "pro" ∈ `["pro"]`, ∉ `["premium","le","70th"]`).

- [ ] **Step 5: Add subset resolution + the full filename marker set**

Add to `EditionResolution`:

```csharp
public static EditionResolution ForSubset(IReadOnlyList<Machine> bases) => new(bases, IsGroupFanOut: false, IsUnresolved: false);
```

Extend `FilenameMarkers` (line 16-22) with the full Stern token set:

```csharp
        ("70th", "70th"), ("60th", "60th"), ("30th", "30th"),
        ("_pro_", "pro"), ("-pro-", "pro"),
        ("_le_", "le"), ("-le-", "le"),
        ("_prem", "premium"), ("-prem", "premium"), ("premium", "premium"),
        ("_sle_", "sle"), ("_ve_", "ve"), ("_vault_", "vault"), ("_brk_", "brk"),
```

Extend `IsGroupLevelDoc` to also accept link_text (add an overload that the linker passes both filename and link_text into; OR the linker concatenates them before calling). Keep the existing `GroupLevelMarkers`.

- [ ] **Step 6: Add subset + group tests**

```csharp
[Fact]
public void Resolve_LePreCombined_MapsToPremiumLeBaseOnly()
{
    var r = EditionResolver.Resolve("Godzilla_LE_Pre_web.pdf", page1Text: null, [Pro, PremLe]);
    Assert.Single(r.Machines);
    Assert.Equal("GweeP-Ml9pZ", r.Machines[0].Id);   // _le_ token ∈ PremLe tokens only
}

[Fact]
public void Resolve_Rulesheet_FansOutToAll()
{
    var r = EditionResolver.Resolve("Godzilla-Rulesheet.pdf", page1Text: null, [Pro, PremLe]);
    Assert.True(r.IsGroupFanOut);
    Assert.Equal(2, r.Machines.Count);
}
```

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~EditionResolverTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Linking/EditionResolver.cs tests/PinballWizard.Application.Tests/Linking/EditionResolverTests.cs
git commit -m "fix(linking) AB#259: EditionResolver matches EditionTokens not Title; subset + full marker set"
```

---

## Task 5: Linker emits `EditionScope`; thread it onto `scraped_documents`

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs` (Tier 2 + page tiers — pass the resolved `EditionScope`; `FanOutAndUpdateAsync` ~line 729-790 — pass scope to the writer)
- Modify: `src/PinballWizard.Core/Models/RawDocument.cs` / `ScrapedDocumentRecord.cs` (add `EditionScope`)
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapedDocumentRepository.cs` (`UpsertFromRawAsync` ~line 56-89 — write `edition_scope`)
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs`

- [ ] **Step 1: Write the failing test (linker records scope)**

Add to `DocumentLinkerTests.cs` (mirror the existing edition tests added this session). Assert the Pro doc links to `GweeP-MW95j` ONLY and the `LinkingResult`/written record carries `EditionScope.SingleEdition`. (The bases now need `EditionTokens` set, matching Task 4's fixtures.)

```csharp
[Fact]
public async Task LinkAsync_GodzillaProDoc_LinksToProOnly_ScopeSingleEdition()
{
    var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla", slug: "godzilla");
    pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
    var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla", slug: "godzilla");
    premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium","le","70th"];
    var raw = MakeRaw(fileUrl: "https://sternpinball.com/.../Godzilla_Pro_web.pdf", sourceType: SourceType.ManualsPage);
    var docWriter = Substitute.For<IScrapedDocumentRepository>();
    var linker = BuildLinker(/* ... */ machines: [pro, premLe], docWriter: docWriter);
    await linker.InitializeAsync(default);
    var result = await linker.LinkAsync(raw, default);

    Assert.Equal(["GweeP-MW95j"], result.LinkedMachineIds);   // Pro ONLY
    await docWriter.Received(1).UpsertFromRawAsync(
        Arg.Any<RawDocumentRecord>(), "GweeP-MW95j", Arg.Any<string>(), Arg.Any<string>(),
        Arg.Any<string?>(), EditionScope.SingleEdition, Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~GodzillaProDoc_LinksToProOnly"`
Expected: FAIL — `UpsertFromRawAsync` has no `EditionScope` parameter (compile error); and/or it fans to both.

- [ ] **Step 3: Add `EditionScope` to the record + writer signature**

In `ScrapedDocumentRecord` (the write-side record in `RawDocument.cs`/`ScrapedDocumentRecord.cs`), add:

```csharp
[JsonPropertyName("edition_scope")] public string? EditionScope { get; init; }
```

In `IScrapedDocumentRepository.UpsertFromRawAsync` and `CosmosScrapedDocumentRepository.UpsertFromRawAsync`, add a parameter `EditionScope editionScope` and write `EditionScope = editionScope.ToString()` (snake-case via a small map, or `"single-edition"/"edition-subset"/"franchise-wide"` strings — match the index value casing in Task 6).

- [ ] **Step 4: Thread scope from resolver → fan-out**

In `DocumentLinker`'s Tier-2 + page-tier blocks, capture the `EditionScope` alongside the resolved machine set (single → `SingleEdition`, `ForSubset` → `EditionSubset`, group fan-out → `FranchiseWide`), carry it on `LinkingResult` (add a field), and pass it into `FanOutAndUpdateAsync` → `UpsertFromRawAsync` for each fanned row.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~DocumentLinkerTests"`
Expected: PASS (all linker tests incl. the new scope assertion).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs src/PinballWizard.Core/Models/ src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapedDocumentRepository.cs tests/PinballWizard.Application.Tests/Linking/DocumentLinkerTests.cs
git commit -m "feat(linking) AB#259: linker emits EditionScope onto scraped_documents"
```

---

## Task 6: Thread `edition`/`edition_scope` into chunks + index schema

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchIndexFields.cs` (add `Edition`, `EditionScope` consts)
- Modify: `src/PinballWizard.Infrastructure/Rag/Indexing/AiSearchIndexSchema.cs` (add 2 filterable String fields, mirror the `MachineId` field at line ~60)
- Modify: the chunk request/record (`Chunk.cs` `ChunkRequest`, `IndexedChunkDocument`) + the ingestion pipeline (`ScrapedDocumentIngestionPipeline.cs` ~line 88-114) + `AiSearchRagIndexer` map — read these and thread `edition`/`editionScope` from `ScrapedDocumentChange` through to the indexed doc
- Test: `tests/PinballWizard.Infrastructure.Tests/Rag/...` (indexer mapping test)

- [ ] **Step 1: Write the failing test (indexer maps the fields)**

Find the existing `AiSearchRagIndexer` mapping test (`grep -rl "MapToDocument\|IndexedChunkDocument" tests`). Add a case asserting a chunk with `Edition="Pro"`, `EditionScope="single-edition"` produces an index document carrying `edition`/`edition_scope`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~Indexer"`
Expected: FAIL — fields absent.

- [ ] **Step 3: Add the field constants + schema fields**

In `AiSearchIndexFields.cs`:

```csharp
    public const string Edition = "edition";
    public const string EditionScope = "edition_scope";
```

In `AiSearchIndexSchema.Build` (mirror the `MachineId` filter/facet field at line ~60):

```csharp
            new(Retrieval.AiSearchIndexFields.Edition, SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new(Retrieval.AiSearchIndexFields.EditionScope, SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
```

- [ ] **Step 4: Thread through chunk → index**

Add `Edition` + `EditionScope` to `ChunkRequest` (`Chunk.cs`) and `IndexedChunkDocument`; populate from `ScrapedDocumentChange` in `ScrapedDocumentIngestionPipeline` (the values now exist on `scraped_documents` from Task 5); set them in `AiSearchRagIndexer.MapToDocument`.

- [ ] **Step 5: Run to verify it passes + full Infra suite**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~Indexer"` then the full `tests/PinballWizard.Infrastructure.Tests`.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Rag/ tests/PinballWizard.Infrastructure.Tests/Rag/
git commit -m "feat(rag) AB#259: thread edition + edition_scope through chunks into the index schema"
```

---

## Task 7: Wizard R1/R2/R3 + surface edition fields

**Files:**
- Modify: the `searchCorpus` hit DTO + `getMachineByTitle` grounding DTO (`MachineGroundingTool.cs`, `SearchCorpusTool` — surface `edition`/`edition_scope` on hits, `EditionLabel`/`EditionTokens` on siblings)
- Modify: the Wizard prompt `Wizard.md` (locate via `grep -rl "getMachineByTitle" src`)
- Test: bUnit/agent tests if present; otherwise a documented manual probe (gated in the live migration)

- [ ] **Step 1: Surface the fields on the tool DTOs**

Add `Edition` + `EditionScope` to the `searchCorpus` hit DTO (read off the index fields added in Task 6); add `EditionLabel` + `EditionTokens` to the `MachineSiblingGroundingDto` (so the Wizard can name editions). Unit-test the DTO mapping where a test exists.

- [ ] **Step 2: Rewrite `Wizard.md` Step 3-4 for R1/R2/R3**

Replace the clarifying-question-first block with the evidence-driven rule (verbatim intent — adapt to the prompt's voice):

```text
After grounding, retrieve corpus chunks (union across sibling bases for
version-dependent + edition-unspecified questions). Inspect the edition_scope
of the hits:
- All relevant hits are franchise-wide → the answer is the same across editions.
  Answer once, and state it applies to all editions (e.g. "For both Pro and
  Premium/LE: ..."). Do NOT silently pick one edition. (R1)
- Hits carry materially different single-edition/edition-subset evidence under
  different bases → answer ALL editions in one response, attributed per edition
  ("For the Pro edition ... (cited: Godzilla Pro Manual); for Premium/LE ...
  (cited: Godzilla Premium/LE Manual)"). Do NOT ask a clarifying question. (R2)
- The user named an edition but the only relevant hits are under a DIFFERENT
  edition → answer honestly with disclosure ("I don't have LE-specific details
  for that, but here's what the Pro manual says ..."). Never silently answer
  from the wrong edition; never blanket-refuse. (R3)
A clarifying question is a LAST RESORT only when answering-all is infeasible.
```

- [ ] **Step 3: Verify build + any Wizard tests**

Run: `dotnet build PinballWizard.slnx -c Release` then any Wizard/Web test suite.
Expected: 0/0; tests green. (Behavioral R1/R2/R3 verification is a live probe in the migration.)

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Application/Ai/ src/PinballWizard.Web/  # adjust to actual Wizard.md path
git commit -m "feat(wizard) AB#259: R1/R2/R3 edition reasoning + surface edition fields on tools"
```

---

## Task 8: Eval rework (edition-aware)

**Files:**
- Modify: `src/PinballWizard.Application/Ai/Evaluation/EvalQuestion.cs` (add fields)
- Create/Modify: evaluators (`AnsweredAllEditionsEvaluator`, `HonestSubstitutionEvaluator`; extend citation evaluators) — read the existing evaluator folder
- Modify: `data/eval/wizard.v1.jsonl` (rewrite Godzilla rows)
- Test: `tests/PinballWizard.Application.Tests/Ai/Evaluation/...`

- [ ] **Step 1: Extend `EvalQuestion`**

```csharp
public sealed record EvalQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("expected_sub_agent")] string ExpectedSubAgent,
    [property: JsonPropertyName("expected_citation_set")] IReadOnlyList<string> ExpectedCitationSet,
    [property: JsonPropertyName("acceptable_refusal")] bool AcceptableRefusal,
    [property: JsonPropertyName("acceptable_citation_sets")] IReadOnlyList<IReadOnlyList<string>>? AcceptableCitationSets = null,
    [property: JsonPropertyName("franchise_wide_ok")] bool FranchiseWideOk = false,
    [property: JsonPropertyName("expected_outcome")] string ExpectedOutcome = "grounded",
    [property: JsonPropertyName("required_editions")] IReadOnlyList<string>? RequiredEditions = null,
    [property: JsonPropertyName("notes")] string? Notes = null);
```

(Keep `ExpectedCitationSet` for back-compat; `AcceptableCitationSets` is the any-of superset.)

- [ ] **Step 2: Write failing evaluator tests + add evaluators**

Read the existing evaluator interface + a sample evaluator. Add `AnsweredAllEditionsEvaluator` (passes when ≥1 citation per `required_editions` AND per-edition attribution present in the answer) and `HonestSubstitutionEvaluator` (passes when the answer discloses the substitution + cites the substitute). Extend `CitationPrecision/Recall` to accept any-of `AcceptableCitationSets` and treat a `franchise-wide` chunk as acceptable when `FranchiseWideOk`. Behavior-asserting tests for each.

- [ ] **Step 3: Rewrite the Godzilla eval rows**

In `data/eval/wizard.v1.jsonl`, replace the 6 all-`GweeP-Ml9pZ` Godzilla rows with edition-aware rows per the spec §6 examples (R1 / R2 / R3 / edition-named). Remove the collapse.

- [ ] **Step 4: Run eval tests**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~Evaluation"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Ai/Evaluation/ data/eval/wizard.v1.jsonl tests/PinballWizard.Application.Tests/Ai/Evaluation/
git commit -m "feat(eval) AB#259: edition-aware eval (any-of citations, answered-all-editions, honest-substitution)"
```

---

## Task 9: Full suite + ADR-0031 correction note

- [ ] **Step 1: Full solution test suite**

Run: `dotnet test PinballWizard.slnx -c Release`
Expected: all green.

- [ ] **Step 2: Annotate ADR-0031**

Add a note to `docs/adr/0031-document-machine-linking-source-of-truth.md` decision #3 that the "edition-qualified Title" assumption is corrected by ADR-0032 (Title is the franchise name; edition discrimination is `EditionTokens`).

- [ ] **Step 3: Commit**

```bash
git add docs/adr/0031-document-machine-linking-source-of-truth.md
git commit -m "docs(adr) AB#259: cross-reference ADR-0032 correction in ADR-0031"
```

---

## Task 10: Live migration (gated; AFTER all code green; user go-ahead per destructive step)

Each step gated; pre-launch (no users), index freely rebuildable. Live env vars per session memory (`AiFoundry__ProjectEndpoint`, `AiSearch__Endpoint`, `Cosmos__AccountEndpoint`, `Cosmos__AccountResourceId`, `Opdb__ApiToken`, `Opdb__BaseUrl`). Use `dotnet exec src/PinballWizard.Cli/bin/Release/net10.0/PinballWizard.Cli.dll`.

- [ ] **Step 1: Catalog re-sync.** `--source opdb`. **Gate:** `tools/probe-godzilla-titles.csx` + a tokens probe show `GweeP-MW95j` EditionTokens `["pro"]`, `GweeP-Ml9pZ` `["premium","le","70th"]`, Title both "Godzilla"; `getMachineByTitle("Godzilla Premium")` → `GweeP-Ml9pZ`.
- [ ] **Step 2: Stale-Sega cleanup.** Delete `G5po2-MeP6B` Sega Godzilla rows from `scraped_documents` (point-delete by partition) + purge their index chunks (AI Search REST `DELETE` by `machine_id` filter, OR rely on the full rebuild in Step 5 producing zero G5po2 chunks if no raw doc links there). **Gate:** AI Search facet shows zero `machine_id=G5po2-MeP6B` chunks after rebuild.
- [ ] **Step 3: Download + re-link.** `--download-documents` then `--relink-all`. **Gate:** `tools/probe-godzilla-docs.csx` shows `Godzilla_Pro_web.pdf` → `GweeP-MW95j` ONLY (not both); `Godzilla-Rulesheet.pdf` → both; `_LE_Pre_` → `GweeP-Ml9pZ`; each row carries the right `edition_scope`.
- [ ] **Step 4: Index rebuild.** Clear `rag_index_state` + `--rebuild-rag-index` + `--run-rag-backfill` + `--sync-metadata-cards`. **Gate:** AI Search facet — Pro-doc chunks under `GweeP-MW95j` with `edition_scope='single-edition'`; rulesheet chunks under both with `'franchise-wide'`; zero G5po2 chunks.
- [ ] **Step 5: Wizard probe.** Via pinwiz.ai OTP gate or local: "How does multiball work in Stern's Godzilla?" → one R2 response attributing both editions. "Godzilla LE flippers" with only Pro data → R3 honest substitution.
- [ ] **Step 6: Eval.** `--eval`. **Gate:** Godzilla R1/R2/R3 rows pass; citation precision materially above the 0.478 baseline.

---

## Self-Review

**Spec coverage:** §1 scope model → Tasks 4-6; §2 catalog discriminator → Tasks 1-2; §3 detection → Task 4; §4 linker + index → Tasks 4-6; §5 Wizard R1/R2/R3 → Task 7; §6 eval → Task 8; §7 migration → Task 10; stale-Sega → Task 10 Step 2. All covered.

**Placeholder scan:** Tasks 3, 5, 6, 7, 8 contain "read the existing X then match its pattern" for `UpdateTitleLookupAsync`, `UpsertFromRawAsync`, the chunk pipeline, the Wizard prompt path, and the evaluator interface — these are files I did not fully open this session. They are flagged as **read-then-match** with the exact method/line to anchor on, not vague placeholders; the engineer reads the named method and mirrors its signature. This is the honest minimum given the breadth; every such spot names the precise anchor.

**Type consistency:** `EditionTokens` (List<string>, lowercase), `EditionLabel` (string?), `EditionScope` (enum SingleEdition/EditionSubset/FranchiseWide; wire strings `single-edition`/`edition-subset`/`franchise-wide`), `EditionResolution.ForSubset`, `DeriveEditionTokens`/`ExtractEditionLabel` — used consistently across Tasks 1-8.

**Open adaptation points (honest):** Tasks 6 & 8 touch the chunk-ingestion pipeline and eval evaluators, which I described from their field shapes (verified) but not their full bodies. The implementing engineer must read `ScrapedDocumentIngestionPipeline.cs`, `AiSearchRagIndexer`, and the evaluator interface and match — flagged in each task.
