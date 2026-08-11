# ADR-0054 Plan 2 — DocumentLinker migration to MachineResolver

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate `DocumentLinker` off its private `ManufacturerSlugs`-derived index and onto the Wave-1 `IMachineResolver`, so every machine in the catalog becomes linkable and genuine ambiguity becomes `needs_review` instead of a silent `not_in_catalog`.

**Architecture:** `MachineResolver` replaces **candidate discovery** only. `EditionResolver` is retained and continues to do **edition disambiguation within a family** — a `ResolutionResult.ResolvedFamily` is handed to `EditionResolver.Resolve` exactly as today's `IsEditionFamily(...)` branch does. Tiers migrate one at a time, each gated on the golden-set replay, in ascending order of trust so the riskiest change lands last with the most evidence behind it.

**Tech Stack:** .NET 10 (SDK pinned `10.0.200`, `rollForward: latestFeature`), C# 13, xUnit, NSubstitute, Azure Cosmos DB, `TreatWarningsAsErrors`.

## Global Constraints

- **Branch naming:** `Dev-PascalCase`. This plan's work: `Dev-Adr0054Wave2*`.
- **Commits:** conventional commits. **No `Co-Authored-By` trailers** (`CONTRIBUTING.md`).
- **`TreatWarningsAsErrors` is on.** A warning fails the build. `dotnet build` must end 0 warnings / 0 errors.
- **Commit author must be** `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>` (gate DLV-01). A repo-local `user.email` is already set.
- **Invariant #17 (`.claude/INVARIANTS.md`):** fallbacks must not hide failures. Every `switch` over `ResolutionResult` **must** carry a `_ =>` arm that **throws**. `ResolutionResult` is convention-closed, not compiler-closed (ADR-0054 "Known limitation"); an unrecognised outcome silently degrading to "no match" is the exact defect the arm prevents.
- **Provenance is sacred.** A document attributed to the wrong machine is worse than one honestly unattributed. Where this plan must choose, it chooses `needs_review` or `not_in_catalog` over a guess.
- **Do NOT "fix" the single-token guard.** It lives in `MachineResolver.IsEligible` and is fed from `MachineIdentityVariants.TrailingQualifiers`. It is the guard that stopped the 1977 Stern machine titled "Pinball" from capturing 172 unrelated documents.
- **Every PR runs:** `/local-review`, `/standards-audit`, then post-push code-scanning triage per `.claude/PR-AUDIT.md`. Gates are HEAD-pinned.
- **Allow 3+ minutes for `git push`** — a pre-push hook runs a full code review and writes `.last-code-review`. It is not a hang. Verify a push with `git ls-remote origin <branch>`, never `$?` after a pipeline.

---

## PRECONDITION — arm the gate before Task 3 ✅ SATISFIED 2026-08-10

**Both preconditions shipped; Tasks 3-8 are unblocked.** The golden-set fixture capture
shipped in PR #801 (`ef2fe57`) — both `.captured.json` fixtures are committed and
`GoldenLinkSet_Replays_WithNoMisattribution` runs for real (543 documents → 734 fan-out
entries at capture). The CLI-image seed packaging shipped in PR #798. The original
precondition text is retained below for context.

ADR-0054 requires the golden link set be captured **from live, before any migration**, and every migration PR gated on it. The capture command:

```bash
dotnet run --project src/PinballWizard.Cli -c Release -- --capture-golden-set
```

This needs live dev Cosmos **data-plane** access. If it fails on authorization, that is issue #744 (developer data-plane RBAC being stripped from `developerObjectId`); the value removed from the deleted local bicepparam was `fb4fdb3e-bc36-44b4-a06c-39627e98183f`. See `tests/PinballWizard.Application.Tests/Fixtures/Linking/CAPTURE.md`.

### Second precondition, found during Task 2: ship the alias seed into the CLI image ✅ shipped in #798

**Discovered 2026-08-09 while implementing Task 2; blocked Task 3, not Task 2.**

`MachineAliasLoader` is **fail-closed** and resolves `data/seeds/machine_aliases.v1.json`
at load time. That file is **not published into the CLI container**:

| | |
|---|---|
| `src/PinballWizard.Api/Dockerfile:49` | `COPY --chown=pinwiz:pinwiz data/seeds/ data/seeds/` ✅ |
| `src/PinballWizard.Cli/Dockerfile` | **no `data/seeds` line at all** ❌ |
| `src/PinballWizard.Cli/*.csproj` | no `<Content Include>` for the seed ❌ |

Task 2 (registration) is safe because the DI factory is lazy — nothing calls `LoadAsync`
yet. **Task 3 makes `DocumentLinker.InitializeAsync` call it**, so without this fix the ACA
linker job throws `FileNotFoundException` at startup and every nightly run dies.

Fix before Task 3 merges — add to the CLI Dockerfile's **runtime** stage, mirroring the API's:

```dockerfile
COPY --chown=pinwiz:pinwiz data/seeds/ data/seeds/
```

Verify by running the built CLI image, not by reading the Dockerfile: a `COPY` in the wrong
stage looks identical in a diff and fails identically at runtime.

Outcome policy the replay enforces (unchanged by this plan):

| Transition | Verdict |
|---|---|
| `linked` → **different machine** | **BLOCKING** — mis-attribution |
| `linked` → `needs_review` | report, do not fail |
| `not_in_catalog` → `linked` | **WIN** — report, do not fail |

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs` | Raw-doc persistence contract | Modify — add `linkReview` param to `UpdateLinkStatusAsync` |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs` | Cosmos implementation | Modify — persist the `LinkReview` block |
| `src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs:241-254` | DI composition root | Modify — register alias loader/catalog; inject resolver deps into `DocumentLinker` |
| `src/PinballWizard.Infrastructure/Resolution/CosmosMachineAliasCatalog.cs` | Binds `IMachineAliasCatalog` to `IMachineRepository` | **Create** |
| `src/PinballWizard.Application/Linking/DocumentLinker.cs` | Tiered linker | Modify — build resolver index; migrate Tiers 2, 3-4, 1; emit `needs_review` |
| `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs` | Migration behaviour tests | **Create** |
| `tests/PinballWizard.Infrastructure.Tests/Persistence/RawDocumentLinkReviewTests.cs` | needs_review persistence | **Create** |

**Out of scope (later Plan-2 slices):** `ScraperReconciliationService`, `MachineGroundingTool`, `TiltForumsGameMatcher`, `KineticistGameResolver`, PB Freshdesk matcher, and `OpdbSyncService` populating `machine_title_lookups`. Each is its own migration with its own gate.

---

### Task 1: Persist the `needs_review` block

`LinkStatus.NeedsReview`, `LinkReviewInfo`, the Cosmos wire model, and the admin queue all shipped in S6 (#774). The **write path does not exist** — `UpdateLinkStatusAsync` takes no review argument, so the linker cannot record why a document needs review. Without this, Task 7 has nowhere to put its candidates.

**Files:**
- Modify: `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs:52-58`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/RawDocumentLinkReviewTests.cs` (create)

**Interfaces:**
- Consumes: `LinkReviewInfo`, `LinkReviewCandidate` (`PinballWizard.Core.Models`, already shipped).
- Produces: `UpdateLinkStatusAsync(string documentId, LinkStatus status, string? resolutionStrategy, string? failureReason, string? overrideId, CancellationToken cancellationToken, LinkReviewInfo? linkReview = null)`. The optional trailing parameter keeps all existing call sites compiling unchanged.

- [x] **Step 1: Write the failing test**

```csharp
// tests/PinballWizard.Infrastructure.Tests/Persistence/RawDocumentLinkReviewTests.cs
[Fact]
public async Task UpdateLinkStatusAsync_WithLinkReview_PersistsCandidates()
{
    var repo = await NewRepositoryWithDocumentAsync("doc-1");

    await repo.UpdateLinkStatusAsync(
        "doc-1", LinkStatus.NeedsReview, "filename", failureReason: null, overrideId: null,
        CancellationToken.None,
        new LinkReviewInfo
        {
            CreatedAt = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc),
            Candidates =
            [
                new LinkReviewCandidate
                {
                    MachineId = "GweeP-MW95j", MachineTitle = "Godzilla (Pro)",
                    EvidenceKind = "Filename", MatchedVariant = "godzilla",
                },
            ],
        });

    var stored = await repo.GetAsync("doc-1", CancellationToken.None);
    Assert.Equal(LinkStatus.NeedsReview, stored!.LinkStatus);
    Assert.Single(stored.LinkReview!.Candidates);
    Assert.Equal("GweeP-MW95j", stored.LinkReview.Candidates[0].MachineId);
    Assert.Equal("godzilla", stored.LinkReview.Candidates[0].MatchedVariant);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter UpdateLinkStatusAsync_WithLinkReview_PersistsCandidates`
Expected: FAIL — compile error, `UpdateLinkStatusAsync` takes 6 arguments not 7.

- [x] **Step 3: Add the parameter to the interface**

```csharp
// IRawDocumentRepository.cs — replace lines 51-58
    // Set link_status and linker metadata on an existing record.
    // linkReview is written ONLY for LinkStatus.NeedsReview; any other status clears it,
    // so a document that leaves review does not keep a stale candidate list.
    Task UpdateLinkStatusAsync(
        string documentId,
        LinkStatus status,
        string? resolutionStrategy,
        string? failureReason,
        string? overrideId,
        CancellationToken cancellationToken,
        LinkReviewInfo? linkReview = null);
```

- [x] **Step 4: Implement in the Cosmos repository**

In `CosmosRawDocumentRepository.UpdateLinkStatusAsync`, add the parameter with the same default, and set the wire field alongside the existing status assignments:

```csharp
        // Write the review block only in NeedsReview; every other status clears it so a
        // resolved document cannot keep a stale candidate list (invariant #17 — a leftover
        // review block would make a linked doc look unresolved in the admin queue).
        cosmos.LinkReview = status == LinkStatus.NeedsReview && linkReview is not null
            ? new RawLinkReviewInfo
            {
                CreatedAt = linkReview.CreatedAt,
                Candidates = linkReview.Candidates
                    .Select(c => new RawLinkReviewCandidate
                    {
                        MachineId = c.MachineId,
                        MachineTitle = c.MachineTitle,
                        EvidenceKind = c.EvidenceKind,
                        MatchedVariant = c.MatchedVariant,
                    })
                    .ToList(),
            }
            : null;
```

- [x] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter RawDocumentLinkReview`
Expected: PASS.

- [x] **Step 6: Run the full suite and build**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 warnings, 0 errors, all green. Existing `UpdateLinkStatusAsync` callers still compile — the new parameter is optional.

- [x] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs \
        tests/PinballWizard.Infrastructure.Tests/Persistence/RawDocumentLinkReviewTests.cs
git commit -m "feat(linking) persist needs_review candidates from the linker (ADR-0054 Wave 2)"
```

---

### Task 2: Register the alias loader and catalog in DI

`MachineAliasLoader` and `IMachineAliasCatalog` shipped in S5 (#770) but **nothing registers them**. Without registration the linker cannot obtain aliases, and `InMemoryMachineIndex.Build` requires the list.

**Files:**
- Create: `src/PinballWizard.Infrastructure/Resolution/CosmosMachineAliasCatalog.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs` (near line 241)
- Test: `tests/PinballWizard.Infrastructure.Tests/Resolution/CosmosMachineAliasCatalogTests.cs` (create)

**Interfaces:**
- Consumes: `IMachineAliasCatalog` (`GroupExistsAsync`, `MachineExistsAsync`), `IMachineAliasLoader.LoadAsync(CancellationToken) → Task<IReadOnlyList<MachineAliasEntry>>`, `IMachineRepository`.
- Produces: `CosmosMachineAliasCatalog(IMachineRepository) : IMachineAliasCatalog`, and DI registrations for `IMachineAliasCatalog` + `IMachineAliasLoader` consumed by Task 3.

- [x] **Step 1: Write the failing test**

```csharp
// tests/PinballWizard.Infrastructure.Tests/Resolution/CosmosMachineAliasCatalogTests.cs
[Fact]
public async Task MachineExistsAsync_ReturnsFalse_WhenManufacturerDiffers()
{
    var machineRepo = Substitute.For<IMachineRepository>();
    machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
        .Returns(new[]
        {
            new Machine
            {
                Id = "GweeP-MW95j", PartitionKey = "stern",
                ManufacturerDisplayName = "stern", Title = "Godzilla (Pro)",
            },
        }.ToAsyncEnumerable());

    var catalog = new CosmosMachineAliasCatalog(machineRepo);

    Assert.True(await catalog.MachineExistsAsync("GweeP-MW95j", "stern", CancellationToken.None));
    Assert.False(await catalog.MachineExistsAsync("GweeP-MW95j", "sega", CancellationToken.None));
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter CosmosMachineAliasCatalog`
Expected: FAIL — `CosmosMachineAliasCatalog` does not exist.

- [x] **Step 3: Implement the catalog**

```csharp
// src/PinballWizard.Infrastructure/Resolution/CosmosMachineAliasCatalog.cs
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Resolution;

namespace PinballWizard.Infrastructure.Resolution;

// Binds IMachineAliasCatalog to the machine repository so MachineAliasLoader can
// fail closed on an alias pointing at a machine or group that does not exist.
// Streams once and caches: the loader validates every seed entry at startup, and a
// per-entry cross-partition query would be one Cosmos round-trip per alias.
public sealed class CosmosMachineAliasCatalog : IMachineAliasCatalog
{
    private readonly IMachineRepository _machineRepo;
    private Dictionary<string, string>? _machineToMfr;
    private Dictionary<string, HashSet<string>>? _groupToMfrs;

    public CosmosMachineAliasCatalog(IMachineRepository machineRepo)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        _machineRepo = machineRepo;
    }

    public async Task<bool> MachineExistsAsync(string machineId, string manufacturerKey, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _machineToMfr!.TryGetValue(machineId, out var mfr)
            && string.Equals(mfr, manufacturerKey, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> GroupExistsAsync(string groupId, string manufacturerKey, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _groupToMfrs!.TryGetValue(groupId, out var mfrs) && mfrs.Contains(manufacturerKey);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_machineToMfr is not null) return;

        var machines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await foreach (var m in _machineRepo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            machines[m.Id] = m.PartitionKey;
            if (m.GroupId is { Length: > 0 } g)
            {
                if (!groups.TryGetValue(g, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    groups[g] = set;
                }
                set.Add(m.PartitionKey);
            }
        }

        _groupToMfrs = groups;
        _machineToMfr = machines;   // assigned LAST — it is the initialised sentinel
    }
}
```

- [x] **Step 4: Register both services**

In `ServiceCollectionExtensions.cs`, immediately **before** the `IDocumentLinker` registration at line 241:

```csharp
        // ADR-0054 resolution core. The loader fails closed on an alias that does not
        // resolve, so it must be able to see the catalog — hence the catalog binding.
        services.AddSingleton<IMachineAliasCatalog>(sp =>
            new CosmosMachineAliasCatalog(sp.GetRequiredService<IMachineRepository>()));

        services.AddSingleton<IMachineAliasLoader>(sp =>
            new MachineAliasLoader(
                sp.GetRequiredService<IMachineAliasCatalog>(),
                sp.GetRequiredService<ILogger<MachineAliasLoader>>()));
```

Before writing this, open `src/PinballWizard.Application/Resolution/MachineAliasLoader.cs` and match its **actual** constructor parameter order and types. If it differs from the two arguments above, use the real signature — do not change the loader to fit this plan.

- [x] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter CosmosMachineAliasCatalog`
Expected: PASS.

- [x] **Step 6: Verify the container actually resolves**

Run: `dotnet test --filter ServiceCollection`
Expected: PASS. If no DI-composition test exists, add one asserting `sp.GetRequiredService<IMachineAliasLoader>()` returns non-null — a registration that throws only at runtime in an ACA job is a 2am failure.

- [x] **Step 7: Commit**

```bash
git add src/PinballWizard.Infrastructure/Resolution/CosmosMachineAliasCatalog.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs \
        tests/PinballWizard.Infrastructure.Tests/Resolution/CosmosMachineAliasCatalogTests.cs
git commit -m "feat(resolution) register machine alias loader and Cosmos alias catalog (ADR-0054 Wave 2)"
```

---

### Task 3: Build the resolver index in `DocumentLinker`, and fix the misleading coverage log

**GATE: the golden-set fixture must be captured before this task merges.**

Build the resolver alongside the existing index and change **no** resolution behaviour. This task exists to make the coverage change measurable before anything depends on it.

The current log at `DocumentLinker.cs:179-182` reports `bySlug.Count` — **slug-having machines only** — even though the title fallback at `:129-150` already indexes slug-less multi-token titles. It therefore *understates its own index*, and is the source of the widely-quoted "87 of 2213". Fix the measurement first, or the migration's before/after numbers are meaningless.

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs` (constructor, `InitializeAsync`)
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs:241-254`
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs` (create)

**Interfaces:**
- Consumes: `InMemoryMachineIndex.Build(IEnumerable<Machine>, IReadOnlyList<MachineAliasEntry>)`, `MachineResolver(InMemoryMachineIndex, IReadOnlyDictionary<string, Machine>)`, `IMachineAliasLoader.LoadAsync`.
- Produces: a private `IMachineResolver? _resolver` and `IReadOnlyDictionary<string, Machine> _machinesById` on `DocumentLinker`, consumed by Tasks 4-7. New optional constructor parameter `IMachineAliasLoader? aliasLoader = null` — when null the resolver is not built and every tier keeps its pre-migration behaviour, so existing tests constructing `DocumentLinker` directly continue to pass untouched.

- [x] **Step 1: Write the failing test**

```csharp
// tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs
[Fact]
public async Task InitializeAsync_WithAliasLoader_IndexesSlugLessMachines()
{
    // A machine with NO ManufacturerSlugs: invisible to the legacy bySlug index.
    var machines = new[] { MakeMachine("AP-Hot-Wheels", "Hot Wheels", "americanpinball") };

    var linker = await BuildLinkerWithResolverAsync(machines);

    // The resolver index must contain variants for this machine even though it has no slugs.
    Assert.True(linker.ResolverVariantCountForTest > 0);
}
```

Add these two helpers to the same file. `MakeMachine` mirrors the private helper in `GoldenLinkSetReplayTests:140-151` but adds `groupId` and makes slugs optional — `Machine.ManufacturerSlugs` already defaults to an empty dictionary, so a slug-less machine needs no argument. **There is no shared `MachineFixtures` class in this repo**; each test file defines its own builder.

```csharp
private static Machine MakeMachine(
    string id, string title, string manufacturer,
    string? groupId = null,
    IDictionary<string, string>? slugs = null)
    => new()
    {
        Id = id,
        PartitionKey = manufacturer,
        ManufacturerDisplayName = manufacturer,
        Title = title,
        GroupId = groupId,
        ManufacturerSlugs = slugs is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(slugs, StringComparer.OrdinalIgnoreCase),
    };
```

`BuildLinkerWithResolverAsync` mirrors `GoldenLinkSetReplayTests.BuildLinkerAsync` (lines 71-101), additionally passing an `IMachineAliasLoader` substitute whose `LoadAsync` returns `Array.Empty<MachineAliasEntry>()`. Copy `MakeRaw` from `GoldenLinkSetReplayTests:104-137` — note its `sourceType` parameter is load-bearing, since `LinkingUtilities.InferManufacturerKey` derives the manufacturer hint from it.

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter InitializeAsync_WithAliasLoader`
Expected: FAIL — no such constructor parameter, no `ResolverVariantCountForTest`.

- [x] **Step 3: Add the resolver build to `InitializeAsync`**

Add fields and constructor parameter:

```csharp
    private readonly IMachineAliasLoader? _aliasLoader;

    // Null until InitializeAsync runs, and stays null when no alias loader was supplied
    // (pre-migration construction path, used by existing tests). Every tier checks for
    // null and falls back to its legacy path, so a partially-migrated linker is never
    // in an undefined state.
    private IMachineResolver? _resolver;
    private IReadOnlyDictionary<string, Machine> _machinesById =
        new Dictionary<string, Machine>(StringComparer.Ordinal);

    // Test-only observability of the built index size. Internal, not public.
    internal int ResolverVariantCountForTest { get; private set; }
```

At the **end** of `InitializeAsync`, after `_machineSlugIndex = slugIndex;`:

```csharp
        // ADR-0054: build the identity-derived index alongside the legacy slug index.
        // Behaviour is unchanged until a tier consults _resolver (Tasks 4-7).
        if (_aliasLoader is not null)
        {
            var aliases = await _aliasLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
            var all = allMachines;                       // captured during the stream below
            var index = InMemoryMachineIndex.Build(all, aliases);
            _machinesById = all.ToDictionary(m => m.Id, StringComparer.Ordinal);
            _resolver = new MachineResolver(index, _machinesById);
            ResolverVariantCountForTest = index.VariantCount;

            _logger.LogInformation(
                "DocumentLinker: resolver index built — {Variants} variants across {Machines} machines (ADR-0054).",
                index.VariantCount, all.Count);
        }
```

To make `allMachines` available, add `var allMachines = new List<Machine>();` before the `await foreach` at line 104 and `allMachines.Add(machine);` immediately after `totalMachines++;`.

- [x] **Step 4: Correct the misleading coverage log**

Replace the `else` branch at `DocumentLinker.cs:178-182`:

```csharp
        else
        {
            // MachinesWithSlugs counts machines reachable via ManufacturerSlugs ONLY.
            // TitleIndexed counts the multi-token-title fallback added for slug-less
            // machines. Reporting only the former understated real coverage and is the
            // source of the long-quoted "87 of 2213" figure.
            _logger.LogInformation(
                "DocumentLinker: indexed {Count} index entries — {MachinesWithSlugs} machines via slugs, "
                + "{TitleIndexed} additional via title fallback (of {Total} total).",
                slugIndex.Count,
                bySlug.Count,
                slugIndex.Select(e => e.Machine.Id).Distinct(StringComparer.Ordinal).Count() - bySlug.Count,
                totalMachines);
        }
```

- [x] **Step 5: Wire the loader into DI**

In `ServiceCollectionExtensions.cs`, inside the `IDocumentLinker` factory, resolve and pass the loader:

```csharp
            // GetRequiredService, not GetService: the loader is registered unconditionally
            // in this same method, so a null could only be a DI regression and must throw,
            // not silently run resolver-less (invariant #17). Implemented this way in Task 3.
            var aliasLoader = sp.GetRequiredService<IMachineAliasLoader>();
            return new DocumentLinker(rawRepo, overrideRepo, machineRepo, linkedRepo, textExtractor, logger,
                cosmosWriteConcurrency: concurrency, blobStore: blobStore, aliasLoader: aliasLoader);
```

- [x] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter DocumentLinkerResolver`
Expected: PASS.

- [x] **Step 7: Run the golden-set replay and the full suite**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 warnings, 0 errors. `GoldenLinkSet_Replays_WithNoMisattribution` **runs** (does not skip) and passes with **zero** `linked → different machine`. If it still skips, the precondition was not met — **stop and capture the fixture**.

- [x] **Step 8: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs \
        src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs \
        tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs
git commit -m "feat(linking) build ADR-0054 resolver index in DocumentLinker; correct coverage log"
```

---

### Task 4: Migrate Tier 2 (filename) to the resolver

Lowest-trust tier, highest gain, and fully covered by the golden set. Migrating it first surfaces mis-attribution risk while the blast radius is one tier.

> **Implementation correction (2026-08-10):** Step 1's first test as written paired an
> `americanpinball` machine with `SourceType.ManualsPage`, which `InferManufacturerKey`
> maps to **stern** — under the fuzzy tiers' hard manufacturer filter (which this plan
> itself says the resolver mirrors) that test is unsatisfiable for any implementation.
> Implemented with `SourceType.AmericanPinballGamePage` plus a strategy assertion
> (`filename_resolver`), and a third, resolver-only red test was added
> (`Tier2_CuratedAlias_LinksAcronymFilename`) because the legacy title fallback already
> links multi-token slug-less titles — the curated-alias path is what only the resolver
> can do.

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs:644-739` (`TryTier2FilenameSlug`)
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs`

**Interfaces:**
- Consumes: `_resolver`, `_machinesById` from Task 3; `ResolutionQuery(string Text, EvidenceKind EvidenceKind, string? ManufacturerHint = null)`; `EvidenceKind.Filename`; `ResolutionResult.{Resolved,ResolvedFamily,Ambiguous,NoMatch}`.
- Produces: `LinkingResult? TryTier2ViaResolver(RawDocumentRecord raw)` — returns `null` to fall through to the page tiers, consumed by no later task but mirrored by Task 5.

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Tier2_LinksSlugLessMachine_ByFranchiseTitle()
{
    var machines = new[] { MakeMachine("AP-Hot-Wheels", "Hot Wheels", "americanpinball") };
    var linker = await BuildLinkerWithResolverAsync(machines);

    var raw = MakeRaw("doc-hw", "https://americanpinball.com/hot-wheels-manual.pdf",
        gameSlug: "", manufacturerKey: "americanpinball", sourceType: SourceType.ManualsPage);

    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.Linked, result.FinalStatus);
    Assert.Equal(["AP-Hot-Wheels"], result.LinkedMachineIds);
}

[Fact]
public async Task Tier2_SingleTokenTrailingQualifier_DoesNotMatch()
{
    // The 172-document incident: a machine literally titled "Pinball" must not absorb
    // every document whose filename contains the word "pinball".
    var machines = new[] { MakeMachine("Stern-Pinball-1977", "Pinball", "stern") };
    var linker = await BuildLinkerWithResolverAsync(machines);

    var raw = MakeRaw("doc-generic", "https://sternpinball.com/service-bulletin-pinball.pdf",
        gameSlug: "", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
    Assert.Empty(result.LinkedMachineIds);
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter Tier2_`
Expected: first test FAILS with `NotInCatalog` (no slug, legacy index cannot reach it). The second may already pass — that is fine and expected; it is a **regression guard**, and it must still pass after Step 3.

- [x] **Step 3: Add the resolver path**

Insert at the top of `TryTier2FilenameSlug`, after `normFilename` is computed:

```csharp
        if (_resolver is not null)
        {
            var viaResolver = TryTier2ViaResolver(raw, filename);
            if (viaResolver is not null) return viaResolver;
        }
```

Then add the method:

```csharp
    // ADR-0054 Tier 2. Filename is FUZZY evidence, so MachineResolver applies a HARD
    // manufacturer filter — matching the pre-migration NarrowToSourceManufacturer contract.
    // Ambiguity returns null here and is converted to needs_review by the caller in Task 7;
    // never guessed.
    private LinkingResult? TryTier2ViaResolver(RawDocumentRecord raw, string filename)
    {
        var mfrKey = LinkingUtilities.InferManufacturerKey(raw.Source);
        var outcome = _resolver!.Resolve(new ResolutionQuery(filename, EvidenceKind.Filename, mfrKey));

        switch (outcome)
        {
            case ResolutionResult.Resolved r:
                _logger.LogDebug("Tier2 resolver: {DocumentId} → {MachineId} via {Variant}.",
                    raw.DocumentId, r.MachineId, r.Evidence.MatchedVariant);
                return new LinkingResult(raw.DocumentId, LinkStatus.Linked, "filename_resolver",
                    [r.MachineId], FailureReason: null);

            case ResolutionResult.ResolvedFamily f:
                // Edition disambiguation still belongs to EditionResolver — the resolver
                // narrows to the family, EditionResolver picks within it.
                var family = f.MachineIds
                    .Where(_machinesById.ContainsKey)
                    .Select(id => _machinesById[id])
                    .ToList();
                return family.Count == 0
                    ? null
                    : ResolveEditionFamily(raw, family, filename, page1Text: null,
                        "filename_resolver_edition", "filename_resolver_edition_group");

            case ResolutionResult.Ambiguous:
                return null;   // Task 7 converts this to needs_review

            case ResolutionResult.NoMatch:
                return null;

            // ResolutionResult is convention-closed, NOT compiler-closed (ADR-0054).
            // Invariant #17: an unrecognised outcome must never degrade into a silent
            // non-attribution — throw so it is seen, not swallowed.
            default:
                throw new InvalidOperationException(
                    $"Unrecognised ResolutionResult '{outcome.GetType().Name}' in Tier 2.");
        }
    }
```

Add `using PinballWizard.Application.Resolution;` to the file's usings.

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter Tier2_`
Expected: BOTH PASS.

- [x] **Step 5: Run the golden-set replay**

Run: `dotnet test --filter GoldenLinkSet`
Expected: PASS with **zero** `linked → different machine`. Report the `not_in_catalog → linked` count in the commit body — that number is the point of the whole migration.

- [x] **Step 6: Full build and suite**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 warnings, 0 errors, all green.

- [x] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs \
        tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs
git commit -m "feat(linking) migrate Tier 2 filename matching to MachineResolver (ADR-0054 Wave 2)"
```

---

### Task 5: Migrate Tiers 3-4 (page text) to the resolver

> **Implementation note (2026-08-10):** in addition to the plan's regression-guard test, a
> resolver-only red test was added (`PageTier_CuratedAlias_LinksAcronymPageText`) — the
> legacy page index already matches multi-token titles, so the curated-alias path is the
> capability only the resolver adds at these tiers. The `Resolved` arm deliberately stamps
> `FranchiseWide` (not the Tier-2 `SingleEdition` routing) because the legacy page tier
> never edition-resolves single matches — behavioural equivalence per tier, not uniformity
> across tiers.

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs:772-867` (`TryMatchPage`)
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs`

**Interfaces:**
- Consumes: `_resolver`, `_machinesById`, `EvidenceKind.PageText`.
- Produces: no new public surface; `TryMatchPage` keeps its signature `(RawDocumentRecord, ExtractedDocument, int, string) → LinkingResult?`.

- [x] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task PageTier_DoesNotLinkAcrossManufacturers()
{
    // Page prose mentions many titles. A Stern manual saying "8 ball" must NOT bind
    // to the Williams machine — PageText is fuzzy, so scoping is a HARD filter.
    var machines = new[] { MakeMachine("Williams-8Ball", "Eight Ball", "williams") };
    var linker = await BuildLinkerWithResolverAsync(machines, pageText: "eight ball is mentioned here");

    var raw = MakeRaw("doc-stern", "https://sternpinball.com/batman-manual.pdf",
        gameSlug: "", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
}
```

`BuildLinkerWithResolverAsync` needs an optional `pageText` parameter that wires an `IDocumentTextExtractor` substitute returning a one-page `ExtractedDocument` with `ExtractionStatus.Success`, plus an `IDocumentBlobStore` substitute whose `TryOpenReadAsync` returns a non-null empty `MemoryStream`. Extend the helper rather than duplicating it.

- [x] **Step 2: Run test to verify it fails or passes for the right reason**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter PageTier_`
Expected: PASS pre-change (the legacy hard filter already does this). It is the guard that the migration must not break. If it FAILS pre-change, stop — the legacy behaviour is not what this plan assumes, and the assumption must be corrected before migrating.

- [x] **Step 3: Add the resolver path to `TryMatchPage`**

After `pageText` is normalized and before the `_machineSlugIndex` LINQ query:

```csharp
        if (_resolver is not null)
        {
            var mfrKeyForQuery = LinkingUtilities.InferManufacturerKey(raw.Source);
            var outcome = _resolver.Resolve(
                new ResolutionQuery(extracted.Pages[pageIndex].Text, EvidenceKind.PageText, mfrKeyForQuery));

            switch (outcome)
            {
                case ResolutionResult.Resolved r:
                    return new LinkingResult(raw.DocumentId, LinkStatus.Linked,
                        $"{strategyName}_resolver", [r.MachineId], FailureReason: null)
                        { EditionScope = EditionScope.FranchiseWide };

                case ResolutionResult.ResolvedFamily f:
                    var family = f.MachineIds.Where(_machinesById.ContainsKey)
                        .Select(id => _machinesById[id]).ToList();
                    if (family.Count > 0)
                    {
                        var viaEdition = ResolveEditionFamily(
                            raw, family, ExtractFilename(raw.Source.FileUrl ?? string.Empty),
                            extracted.Pages[pageIndex].Text,
                            $"{strategyName}_resolver_edition", $"{strategyName}_resolver_edition_group");
                        if (viaEdition is not null) return viaEdition;
                    }
                    break;

                case ResolutionResult.Ambiguous:
                case ResolutionResult.NoMatch:
                    break;   // fall through to the legacy index below

                default:
                    throw new InvalidOperationException(
                        $"Unrecognised ResolutionResult '{outcome.GetType().Name}' in {strategyName}.");
            }
        }
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter PageTier_`
Expected: PASS.

- [x] **Step 5: Golden-set replay + full suite**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 warnings, 0 errors; `GoldenLinkSet` green with zero mis-attributions.

- [x] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs \
        tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs
git commit -m "feat(linking) migrate page-text tiers to MachineResolver (ADR-0054 Wave 2)"
```

---

### Task 6: Migrate Tier 1 (provenance slug) to the resolver

Highest-trust tier, migrated last.

> **Implementation note (2026-08-10):** in addition to the plan's soft-preference
> regression guard, a resolver-only red test was added
> (`Tier1_ProvenanceSlug_LinksSlugLessMachine_ViaResolver`) — the legacy `_machinesBySlug`
> index is built from `ManufacturerSlugs` alone, so a slug-less machine is unreachable by
> provenance slug pre-migration; the resolver's title-derived variants reach it. `EvidenceKind.ProvenanceSlug` makes manufacturer scoping a **soft preference**, which preserves the deliberate `PreferByManufacturer`-vs-`NarrowToSourceManufacturer` split documented at `DocumentLinker.cs:494-508`.

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs:529-613`
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs`

**Interfaces:**
- Consumes: `_resolver`, `EvidenceKind.ProvenanceSlug`.
- Produces: no new public surface.

- [x] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Tier1_ProvenanceSlug_LinksToOtherManufacturer_WhenSoleCandidate()
{
    // Regression guard for LinkAsync_Tier1Xref_NoSternCandidate_DoesNotRegress:
    // provenance scoping is a SOFT preference, so a Stern-sourced doc whose slug
    // resolves only to a Sega machine still links.
    var machines = new[]
    {
        MakeMachine("Sega-Godzilla-1998", "Godzilla", "sega",
            slugs: new Dictionary<string, string> { ["sega"] = "godzilla" }),
    };
    var linker = await BuildLinkerWithResolverAsync(machines);

    var raw = MakeRaw("doc-gz", "https://sternpinball.com/doc.pdf",
        gameSlug: "godzilla", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.Linked, result.FinalStatus);
    Assert.Equal(["Sega-Godzilla-1998"], result.LinkedMachineIds);
}
```

- [x] **Step 2: Run test to verify it passes pre-change**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter Tier1_ProvenanceSlug`
Expected: PASS. This is the behaviour that must survive; if it fails now, the migration premise is wrong — stop.

- [x] **Step 3: Route the game slug through the resolver**

In `TryTier1ProvenanceSlug`, replace the `_machinesBySlug.TryGetValue(gameSlug, ...)` branch body with a resolver call when `_resolver is not null`, keeping the legacy lookup as the fallback:

```csharp
        if (raw.Game?.Slug is { Length: > 0 } gameSlug && seenSlugs.Add(gameSlug))
        {
            if (_resolver is not null)
            {
                var outcome = _resolver.Resolve(
                    new ResolutionQuery(gameSlug, EvidenceKind.ProvenanceSlug, mfrHint));

                resolved = outcome switch
                {
                    ResolutionResult.Resolved r => new LinkingResult(
                        raw.DocumentId, LinkStatus.Linked, "game_slug_resolver",
                        [r.MachineId], FailureReason: null),

                    ResolutionResult.ResolvedFamily f => ResolveEditionFamily(
                        raw,
                        f.MachineIds.Where(_machinesById.ContainsKey).Select(id => _machinesById[id]).ToList(),
                        filename, page1Text: null,
                        "game_slug_resolver_edition", "game_slug_resolver_edition_group"),

                    ResolutionResult.Ambiguous => null,
                    ResolutionResult.NoMatch => null,

                    // Invariant #17 — never silently degrade an unknown outcome.
                    _ => throw new InvalidOperationException(
                        $"Unrecognised ResolutionResult '{outcome.GetType().Name}' in Tier 1."),
                };
            }

            if (resolved is null && _machinesBySlug.TryGetValue(gameSlug, out var gameCandidates))
            {
                resolved = ResolveSlugToResult(
                    raw, gameCandidates, filename, mfrHint,
                    "game_slug", "game_slug_edition", "game_slug_edition_group");
            }
        }
```

Guard: `ResolveEditionFamily` returns `null` on an empty candidate list, so the legacy fallback still runs — do not let an empty family short-circuit the tier.

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter Tier1_`
Expected: PASS.

- [x] **Step 5: Golden-set replay + full suite**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 warnings, 0 errors; `GoldenLinkSet` green with zero mis-attributions. Tier 1 carries most currently-linked documents — a regression here is the most likely place for the gate to fire. **If it fires, revert this task and investigate; do not weaken the gate.**

- [x] **Step 6: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs \
        tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs
git commit -m "feat(linking) migrate Tier 1 provenance slug to MachineResolver (ADR-0054 Wave 2)"
```

---

### Task 7: Emit `needs_review` instead of silently dropping ambiguity

Today an ambiguous match becomes `NotInCatalog` with no record of the candidates.

> **Implementation corrections (2026-08-10):** (1) the plan's `_lastAmbiguous` instance
> field is a data race — `RunBatchAsync` runs `LinkAsync` concurrently
> (`Parallel.ForEachAsync`, `MaxDegreeOfParallelism = _cosmosWriteConcurrency`), so one
> document's ambiguity could stamp another document's review record. Implemented as a
> per-call `AmbiguityCapture` object threaded through the (synchronous) tier methods
> instead. (2) The legacy Tier-2 ambiguous bail returns `NotInCatalog` directly and
> short-circuits the no-tier-matched path the plan converts — when the resolver also saw
> the ambiguity, that bail now defers (returns null) so the conversion runs; the
> resolver-less path is byte-identical (pinned by the pre-existing
> `LinkAsync_Tier2FilenameSlug_AmbiguousMatch_NotInCatalog`). (3)
> `LinkingNeedsReviewTotal.Add` carries the manufacturer + evidence_kind tags its
> declaration documents — the plan's bare `.Add(1)` answered the open question wrongly.
> (4) `RunBatchAsync`'s tuple gained a `NeedsReview` bucket (interface + CLI summary
> updated) rather than counting needs_review documents in no bucket. ADR-0054 §5: ambiguity is never guessed, and it must become visible and curatable. The S6 admin queue already exists and is waiting for this data.

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs` (`LinkAsync` no-match path, lines 290-318)
- Test: `tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs`

**Interfaces:**
- Consumes: `LinkReviewInfo`/`LinkReviewCandidate` (Core), `UpdateLinkStatusAsync(..., LinkReviewInfo?)` from Task 1, `ResolutionResult.Ambiguous`.
- Produces: private `LinkReviewInfo BuildReview(ResolutionResult.Ambiguous ambiguous)`.

- [x] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Ambiguous_WritesNeedsReview_WithCandidates()
{
    // Two same-manufacturer machines, different GroupIds → not an edition family → Ambiguous.
    var machines = new[]
    {
        MakeMachine("Stern-A", "Mystery Machine", "stern", groupId: "GrpA"),
        MakeMachine("Stern-B", "Mystery Machine", "stern", groupId: "GrpB"),
    };
    var linker = await BuildLinkerWithResolverAsync(machines);

    var raw = MakeRaw("doc-amb", "https://sternpinball.com/mystery-machine.pdf",
        gameSlug: "", manufacturerKey: "stern", sourceType: SourceType.ManualsPage);

    var result = await linker.LinkAsync(raw, CancellationToken.None);

    Assert.Equal(LinkStatus.NeedsReview, result.FinalStatus);
    Assert.Empty(result.LinkedMachineIds);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter Ambiguous_WritesNeedsReview`
Expected: FAIL — actual is `NotInCatalog`.

- [x] **Step 3: Capture the ambiguous outcome and stamp NeedsReview**

Add a field `private ResolutionResult.Ambiguous? _lastAmbiguous;` — set it in each tier's `Ambiguous` arm before returning null, and clear it at the top of `LinkAsync`. Then in the no-tier-matched path replace the `noMatchResult` construction:

```csharp
        // Ambiguity is never guessed (ADR-0054 §5). If any tier saw multiple plausible
        // non-family candidates, record them for the admin review queue rather than
        // reporting an honest-looking NotInCatalog that hides a real decision.
        if (_lastAmbiguous is { } ambiguous)
        {
            var review = new LinkReviewInfo
            {
                CreatedAt = DateTime.UtcNow,
                Candidates = ambiguous.Candidates.Select(c => new LinkReviewCandidate
                {
                    MachineId = c.MachineId,
                    MachineTitle = c.MachineTitle,
                    EvidenceKind = ambiguous.Evidence.EvidenceKind.ToString(),
                    MatchedVariant = c.MatchedVariant,
                }).ToList(),
            };

            var reviewResult = new LinkingResult(
                raw.DocumentId, LinkStatus.NeedsReview, ResolutionStrategy: null,
                LinkedMachineIds: [],
                FailureReason: $"Ambiguous: {ambiguous.Candidates.Count} candidates");

            await PruneStaleFanOutRowsAsync(raw.DocumentId, new HashSet<string>(), cancellationToken)
                .ConfigureAwait(false);

            await _rawRepo.UpdateLinkStatusAsync(
                raw.DocumentId, reviewResult.FinalStatus, reviewResult.ResolutionStrategy,
                reviewResult.FailureReason, overrideId: null, cancellationToken, review)
                .ConfigureAwait(false);

            PinballWizardTelemetry.LinkingNeedsReviewTotal.Add(1);

            DocumentsProcessedCounter.Add(1,
                new KeyValuePair<string, object?>("resolution_strategy", "none"),
                new KeyValuePair<string, object?>("link_status", "needs_review"));

            return reviewResult;
        }
```

Also add `LinkStatus.NeedsReview` to the `RunBatchAsync` switch (it currently has no arm, so needs-review documents would be counted in none of the buckets) and to the idempotency skip-set at line 190 — a document awaiting human review must not be re-linked on the next run.

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter Ambiguous_`
Expected: PASS.

- [x] **Step 5: Verify `needs_review` is NOT counted as a regression**

Run: `dotnet test --filter GoldenLinkSet`
Expected: PASS. `linked → needs_review` is report-only by the harness's own policy; if it fails the build, the harness policy was misread — re-read `GoldenLinkSetReplayTests` lines 28-32 before changing anything.

- [x] **Step 6: Full build and suite**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 warnings, 0 errors.

- [x] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs \
        tests/PinballWizard.Application.Tests/Linking/DocumentLinkerResolverTests.cs
git commit -m "feat(linking) emit needs_review with candidates on ambiguous resolution (ADR-0054 Wave 2)"
```

---

### Task 8: Retire the legacy slug index

Only after Tasks 4-6 have replayed clean. Removing it earlier destroys the fallback that makes each preceding task independently revertible.

> **Implementation notes (2026-08-10):**
> - **Plan gap: the xref loop.** `TryTier1ProvenanceSlug`'s cross-reference branch also
>   consumed `_machinesBySlug`, which this task deletes, but the plan never said what to
>   do with it. Migrated it through the resolver identically to the game slug
>   (`ProvenanceSlug` evidence, `xref_slug_resolver*` strategies) via a shared
>   `ResolveSlugViaResolver` helper; the multi-slug ambiguity guard is unchanged.
> - **Page-tier unresolved-family fan-out preserved.** The legacy page tier deliberately
>   fanned an edition family out to all bases when no edition signal existed ("rather
>   than guess" — one game, attribution-safe). The resolver's `ResolvedFamily` arm now
>   does the same instead of returning null, which would have regressed those docs.
> - **Behavioural deltas, all gate-verified:** cross-game page-text multiplicity and
>   filename ambiguity now become `needs_review` (ADR §5) instead of the legacy
>   fan-out-to-all / `NotInCatalog` bail; strategy names are uniformly `*_resolver*`.
>   Golden-set replay after retirement: **zero `linked → different machine`, needs-review
>   562 → 267** — 295 previously-unresolvable entries now link, each to its expected
>   machine.
> - The Task-7 evidence-kind deference in the legacy bail was deleted along with the
>   bail itself; the cross-tier leak test was repurposed to pin the new contract
>   (`Tier1Ambiguity_SurfacesAsNeedsReview_WithTier1Candidates`).
> - The cross-manufacturer slug-collision warning was kept as a standalone transient
>   scan in `InitializeAsync` (observability only, not a matching index).

**Files:**
- Modify: `src/PinballWizard.Application/Linking/DocumentLinker.cs` — delete `_machineSlugIndex`, `_machinesBySlug`, the title-fallback block at `:129-150`, `FindMachineById`'s index scan.

**Interfaces:**
- Consumes: `_resolver`, `_machinesById`.
- Produces: `FindMachineById` becomes an `_machinesById` dictionary lookup — O(1) instead of the O(n) scan at `:983-990`.

- [x] **Step 1: Make the resolver non-optional**

Change the constructor parameter from `IMachineAliasLoader? aliasLoader = null` to a required `IMachineAliasLoader aliasLoader`, and update every test constructing `DocumentLinker` directly (including `GoldenLinkSetReplayTests.BuildLinkerAsync`) to pass a substitute.

- [x] **Step 2: Run the full suite to find every call site**

Run: `dotnet build -c Release`
Expected: FAIL, listing each `new DocumentLinker(...)` needing the argument. Fix each; do not add a default back.

- [x] **Step 3: Delete the legacy index and its consumers**

Remove `_machinesBySlug`, `_machineSlugIndex`, the slug/title index build in `InitializeAsync`, and the legacy fallback branches added in Tasks 4-6. Replace `FindMachineById` with:

```csharp
    private Machine? FindMachineById(string machineId) =>
        _machinesById.TryGetValue(machineId, out var m) ? m : null;
```

Keep `PreferByManufacturer`, `NarrowToSourceManufacturer`, and `IsEditionFamily` **only** if a remaining caller uses them; delete any that are now unreferenced (`TreatWarningsAsErrors` will not flag unused private methods, so check by search, not by build).

- [x] **Step 4: Run the full suite**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 warnings, 0 errors, all green including the golden-set replay.

- [x] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Linking/DocumentLinker.cs tests/
git commit -m "refactor(linking) retire the legacy ManufacturerSlugs index (ADR-0054 Wave 2)"
```

---

## After the plan: the live run

The code change alone does not fix the 252 stuck documents. Once merged and deployed:

```bash
# 1. Reset algorithm-derived terminal states (NOT ManuallyLinked / PlatformGeneric)
dotnet run --project src/PinballWizard.Cli -c Release -- --relink-all
```

Then verify against ADR-0054's own acceptance bar — **not "the tests pass"**, because #752's tests passed:

- The corpus-coverage probe (#748/#749) reports **zero source gaps**, closing the `ap` gap.
- The golden link set replays with **no** `linked → different machine`.

Expect a burst of `needs_review` documents on the first run. That is the design working: previously-silent ambiguity becoming visible. Triage them through `/admin/link-review`.

**Related open issues this should close or materially advance:** #745, #749, #655. **Does not address** the linker OOM (`PdfPigDocumentTextExtractor.Extract` buffering whole blobs via `BlobDocumentStore.TryOpenReadAsync`) — that is a separate bug, fixed by streaming, not by raising memory.

---

## Self-Review

**Spec coverage against ADR-0054's seven decisions:**

| ADR decision | Covered |
|---|---|
| 1. One normalizer | Shipped in Wave 1 (S0). Consumed transitively via `MachineVariant.Create`. |
| 2. Canonical identity is the join key | Tasks 3-6. |
| 3. Curated aliases first-class | Task 2 (wiring); seed shipped S5. |
| 4. Confidence-tiered, evidence-aware | Tasks 4-6 (`EvidenceKind` per tier). |
| 5. Ambiguity never guessed | Task 7. |
| 6. One variant generator feeds both stores | **Out of scope** — `OpdbSyncService`/`machine_title_lookups` is a later Plan-2 slice. |
| 7. `UpsertRawAsync` field split (#762) | **Out of scope** — separate Wave-1 stream (S2), independent of the linker. |

Decisions 6 and 7 are deliberately deferred, not missed; each is its own consumer migration with its own gate, per the ADR's "contract-first, then parallel" strategy.

**Placeholder scan:** no TBD/TODO; every code step carries real code; no "similar to Task N" references.

**Type consistency:** `ResolutionQuery`, `ResolutionResult.{Resolved,ResolvedFamily,Ambiguous,NoMatch}`, `EvidenceKind`, `MachineVariant`, `InMemoryMachineIndex.Build`, `MachineResolver(..)`, `IMachineAliasLoader.LoadAsync`, `LinkReviewInfo`/`LinkReviewCandidate`, `LinkingResult(..)`, and `UpdateLinkStatusAsync` were each read from source before use.

One assumption was caught and corrected during self-review: an earlier draft called `MachineFixtures.Create(...)`. **No such class exists** — this repo has no shared machine fixture; `GoldenLinkSetReplayTests` defines a private `MakeMachine` at lines 140-151. All call sites now use the local `MakeMachine` helper defined in Task 3, whose shape was verified against `src/PinballWizard.Core/Domain/Machine.cs` (`Id`/`PartitionKey`/`ManufacturerDisplayName`/`Title` required; `GroupId` settable; `ManufacturerSlugs` defaults to an empty `OrdinalIgnoreCase` dictionary).

Two items still to verify at implementation time, both flagged inline rather than guessed:
- `MachineAliasLoader`'s real constructor signature (Task 2 Step 4).
- `PinballWizardTelemetry.LinkingNeedsReviewTotal`'s tag expectations (Task 7 Step 3) — it is declared at `PinballWizardTelemetry.cs:672`; confirm whether it expects tags before calling `.Add(1)` bare.
