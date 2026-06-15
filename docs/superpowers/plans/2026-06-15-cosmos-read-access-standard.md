# Cosmos Read-Access Standard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish and enforce the project-wide Cosmos read-access standard (ADR-0036) by making cross-partition queries syntactically explicit and pinning every one to a reviewed allow-list via an architecture test.

**Architecture:** Generalizes ADR-0025's machines-scoped CQRS decision into a standing standard (tiers T0–T3). Enforcement is structural: the base repository exposes a partition-scoped `StreamAsync` (non-nullable partition key) and a separate `StreamCrossPartitionAsync`; an architecture test asserts that only allow-listed sites call the cross-partition method, so a new cross-partition query fails CI until consciously reviewed and added.

**Tech Stack:** .NET 10, C#, Azure Cosmos SDK (`Microsoft.Azure.Cosmos`), xUnit. Plan is doc + a base-repository refactor + a source-scanning architecture test. No Cosmos emulator needed (existing integration tests already cover the streaming behavior; this plan only renames the entry points and adds a static-source guard).

This is **Plan 1 of 2**. Plan 2 (`2026-06-15-admin-machine-catalog.md`) implements the Admin Machine Catalog as the first consumer and depends on this plan landing first.

---

## File Structure

- **Create:** `docs/adr/0036-cosmos-read-access-standard.md` — the standard (MADR-lite ADR).
- **Modify:** `src/PinballWizard.Application/Persistence/IRepository.cs` — make `StreamAsync` partition key non-nullable; add `StreamCrossPartitionAsync`.
- **Modify:** `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRepository.cs` — split the streaming implementation.
- **Modify (call-site migration):** the cross-partition callers identified in the inventory — `MachineRepository.cs`, `CosmosScrapedDocumentRepository.cs`, `CosmosRawDocumentRepository.cs`, `FeaturedMachineRepository.cs`, `CosmosLinkOverrideRepository.cs`, `CosmosAdminSettingsRepository.cs`.
- **Create:** `tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs` — the enforcement test.
- **Modify:** `.claude/INVARIANTS.md` — record the standard as a locked invariant.

---

### Task 1: Author ADR-0036 (the standard)

**Files:**
- Create: `docs/adr/0036-cosmos-read-access-standard.md`
- Modify: `docs/adr/README.md` (index — add the 0036 row)

- [ ] **Step 1: Write the ADR**

Lift the "Part 1" content from `docs/superpowers/specs/2026-06-15-cosmos-data-access-and-admin-catalog-design.md` into a MADR-lite ADR. Required sections: Status (Accepted), Date (2026-06-15), Deciders (Jim Keeley), Context, Decision (the four tiers + dual-write vs change-feed sub-rule + event-sourcing-deferred), Consequences, References (ADR-0025, ADR-0007, ADR-0031). Include the mermaid selection flowchart from the spec (mermaid, not ASCII — per repo docs standard). State explicitly that this ADR **generalizes** ADR-0025 (does not supersede it).

- [ ] **Step 2: Add the index row**

In `docs/adr/README.md`, add the `0036` row following the existing table format.

- [ ] **Step 3: Verify the doc-conformance test still passes**

Run: `dotnet test --filter "FullyQualifiedName~Adr" ` (the repo's ADR/doc-conformance tests — they assert the index and ADR front-matter are consistent; do not hardcode the ADR range).
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add docs/adr/0036-cosmos-read-access-standard.md docs/adr/README.md
git commit -m "docs(adr) AB#259: ADR-0036 Cosmos read-access standard"
```

---

### Task 2: Make the partition-scoped vs cross-partition distinction explicit on the repository contract

**Files:**
- Modify: `src/PinballWizard.Application/Persistence/IRepository.cs:60-64`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRepository.cs:131-174`

- [ ] **Step 1: Update the interface**

In `IRepository.cs`, replace the single `StreamAsync` declaration (lines 60-64) with a non-nullable partition-scoped method plus an explicit cross-partition method:

```csharp
    /// <summary>
    /// Stream documents matching a SQL query within a SINGLE partition
    /// (Tier 1 per ADR-0036). Pages are pulled lazily.
    /// </summary>
    /// <param name="partitionKey">Partition to scope the query to. Required — a single-partition read.</param>
    IAsyncEnumerable<T> StreamAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        string partitionKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stream documents matching a SQL query ACROSS ALL PARTITIONS
    /// (fan-out). Per ADR-0036 this is a Tier 2 escape hatch: permitted
    /// ONLY for back-office/admin/startup paths over a provably bounded
    /// set, and every call site MUST be listed in
    /// CrossPartitionQueryAllowListTests. User-facing or unbounded
    /// aggregate reads MUST use a Tier 3 projection instead.
    /// </summary>
    IAsyncEnumerable<T> StreamCrossPartitionAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken);
```

- [ ] **Step 2: Update the implementation**

In `CosmosRepository.cs`, replace the single public `StreamAsync` (lines 131-174) with two public methods delegating to a shared private core. The private core is the existing body, with `partitionKey` nullable internally:

```csharp
    /// <inheritdoc />
    public IAsyncEnumerable<T> StreamAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        return StreamCoreAsync(query, parameters, partitionKey, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<T> StreamCrossPartitionAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken)
        => StreamCoreAsync(query, parameters, partitionKey: null, cancellationToken);

    private async IAsyncEnumerable<T> StreamCoreAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters,
        string? partitionKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var queryDefinition = new QueryDefinition(query);
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                queryDefinition = queryDefinition.WithParameter('@' + name, value);
            }
        }

        var requestOptions = new QueryRequestOptions();
        if (partitionKey is not null)
        {
            requestOptions.PartitionKey = new PartitionKey(partitionKey);
        }

        using var iterator = _container.GetItemQueryIterator<T>(queryDefinition, requestOptions: requestOptions);
        while (iterator.HasMoreResults)
        {
            var page = await ExecuteWithMetricsAsync(
                "query",
                async ct =>
                {
                    var p = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                    return (p, p.RequestCharge);
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var item in page)
            {
                yield return item;
            }
        }
    }
```

- [ ] **Step 3: Build — expect the call sites to fail to compile**

Run: `dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj`
Expected: FAIL — the existing cross-partition callers pass `null` to `StreamAsync` (now non-nullable) and the partition-scoped callers that passed a non-null value still compile. The compiler errors are the migration checklist for Task 3.

- [ ] **Step 4: Commit (after Task 3 compiles — do not commit a broken build)**

Deferred to Task 3 Step 4.

---

### Task 3: Migrate the existing cross-partition call sites

**Files (each is a known cross-partition site from the 2026-06-15 inventory):**
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/MachineRepository.cs` — `StreamAllAsync`, `QueryByTitleAsync`, `GetSiblingsByGroupIdAsync`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapedDocumentRepository.cs` — `StreamByDocumentIdAsync`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs` — `StreamByStatusAsync`, `StreamAllAsync`, `StreamBySourcePatternAsync`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/FeaturedMachineRepository.cs` — `GetAllAsync`, `GetAllDocumentsAsync`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosLinkOverrideRepository.cs` — `LoadAllAsync`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosAdminSettingsRepository.cs` — `GetAllAsync`

- [ ] **Step 1: Replace each `StreamAsync(query, params, null, ct)` with `StreamCrossPartitionAsync(query, params, ct)`**

For every compiler error from Task 2 Step 3, change the call from the partition-scoped overload (passing `null`) to the cross-partition overload (drop the `null` argument). Example, in `MachineRepository.cs` `StreamAllAsync`:

```csharp
// before:
return StreamAsync("SELECT * FROM c", parameters: null, partitionKey: null, cancellationToken);
// after:
return StreamCrossPartitionAsync("SELECT * FROM c", parameters: null, cancellationToken);
```

Leave partition-scoped callers (those passing a real partition key, e.g. `StreamByManufacturerAsync`) unchanged.

- [ ] **Step 2: Build clean**

Run: `dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj`
Expected: PASS — zero errors.

- [ ] **Step 3: Run the existing Infrastructure tests to prove behavior is unchanged**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj`
Expected: PASS — the rename is behavior-preserving; existing repository tests (StreamAllAsync, GetSiblings, etc.) still pass.

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Application/Persistence/IRepository.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/
git commit -m "refactor(infra) AB#259: explicit StreamCrossPartitionAsync surface (ADR-0036 enforcement)"
```

---

### Task 4: The enforcement architecture test

**Files:**
- Create: `tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs`

- [ ] **Step 1: Write the failing test**

This test scans the Cosmos repository source for `StreamCrossPartitionAsync(` call sites and asserts the set of files containing them equals a documented allow-list. A new cross-partition query in a new file fails the test until a reviewer adds it (a conscious act). Uses the repo-root walk convention from `PreRenderedDiagramTests`.

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Architecture;

// Enforces ADR-0036: cross-partition Cosmos queries (Tier 2) are a
// conscious, reviewed exception. Every file that calls
// StreamCrossPartitionAsync MUST appear in the allow-list below with a
// justification. A new call site in a new file fails this test until a
// reviewer adds it here — making the cross-partition decision explicit.
public sealed class CrossPartitionQueryAllowListTests
{
    // Justified Tier 2 sites (2026-06-15 inventory). Key = file name;
    // value = the documented justification (bound / why back-office).
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        ["MachineRepository.cs"] = "linker slug-index build (StreamAll), title fallback, sibling lookup (1-10) — back-office",
        ["CosmosScrapedDocumentRepository.cs"] = "fan-out machine_id lookup by document — admin/relink path",
        ["CosmosRawDocumentRepository.cs"] = "linker batch by status, downloader StreamAll, override source-pattern — back-office",
        ["FeaturedMachineRepository.cs"] = "landing strip, bounded ~6 docs (ADR-0025 §6)",
        ["CosmosLinkOverrideRepository.cs"] = "startup cache-load, bounded <1k",
        ["CosmosAdminSettingsRepository.cs"] = "admin settings page, bounded tens",
    };

    [Fact]
    public void EveryCrossPartitionCallSite_IsInTheAllowList()
    {
        var cosmosDir = Path.Combine(RepoRoot(), "src", "PinballWizard.Infrastructure", "Persistence", "Cosmos");
        var callRegex = new Regex(@"\.StreamCrossPartitionAsync\s*\(", RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(cosmosDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name == "CosmosRepository.cs") continue; // the definition itself
            if (!callRegex.IsMatch(File.ReadAllText(file))) continue;
            if (!AllowList.ContainsKey(name))
                offenders.Add(name);
        }

        Assert.True(
            offenders.Count == 0,
            "New cross-partition Cosmos query found outside the ADR-0036 allow-list: " +
            string.Join(", ", offenders) +
            ". If this is a justified Tier 2 read, add it to AllowList with a bound/justification. " +
            "If it is a user-facing or unbounded aggregate, use a Tier 3 projection instead.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate repo root (no PinballWizard.slnx walking up).");
        return dir.FullName;
    }
}
```

- [ ] **Step 2: Run — verify it passes with the migrated call sites**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CrossPartitionQueryAllowListTests"`
Expected: PASS — all migrated sites are in the allow-list.

- [ ] **Step 3: Prove the guard bites (temporary negative check)**

Temporarily add `_ = StreamCrossPartitionAsync;`-style usage in a non-allow-listed Cosmos file (e.g. a scratch line in `IngestionSourceRepository.cs`), re-run the test, and confirm it FAILS naming that file. Then revert the scratch line.
Expected: FAIL naming `IngestionSourceRepository.cs`, then PASS after revert.

- [ ] **Step 4: Commit**

```bash
git add tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs
git commit -m "test(infra) AB#259: ADR-0036 cross-partition query allow-list guard"
```

> **Note (post-delivery):** The delivered test was deliberately broadened beyond the sketch above in two ways: (a) it detects BOTH `StreamCrossPartitionAsync` AND direct `GetItemQueryIterator<` calls (the direct-iterator escape hatch is also a cross-partition mechanism); and (b) it scans the entire `src/PinballWizard.Infrastructure` tree rather than just `Persistence/Cosmos`, so cross-partition queries that live outside the repository layer (e.g. `CosmosAiSearchRagReconciler.cs`) are caught too. A standard with a bypass is the failure mode the test exists to prevent.

---

### Task 5: Record the standard as a locked invariant

**Files:**
- Modify: `.claude/INVARIANTS.md`

- [ ] **Step 1: Add the invariant**

Add an entry: *"Cosmos reads follow the ADR-0036 tier model. Cross-partition queries go through `StreamCrossPartitionAsync` and must be allow-listed in `CrossPartitionQueryAllowListTests`; user-facing/unbounded aggregates use a Tier 3 change-feed projection. See ADR-0036."* Link ADR-0036.

- [ ] **Step 2: Verify invariants doc-conformance test (if any) passes**

Run: `dotnet test --filter "FullyQualifiedName~Invariant"` (if the repo has an invariants conformance test; otherwise skip).
Expected: PASS or no-matching-tests.

- [ ] **Step 3: Commit**

```bash
git add .claude/INVARIANTS.md
git commit -m "docs AB#259: record ADR-0036 Cosmos read-access invariant"
```

---

## Self-Review

**Spec coverage:** Part 1 of the spec (tiers, dual-write vs change-feed sub-rule, event-sourcing deferral, enforcement via architecture test) → Tasks 1 (ADR), 2-3 (explicit cross-partition surface), 4 (allow-list test), 5 (invariant). The dual-write/change-feed sub-rule and ES deferral are documented in the ADR (Task 1); they need no code in this plan (Plan 2 exercises the change-feed projection path). Covered.

**Placeholder scan:** No TBD/TODO. Every code step shows the actual code. The ADR content is delegated to "lift from the spec Part 1" with the exact required sections enumerated — acceptable because the source content exists and is approved.

**Type consistency:** `StreamAsync(query, parameters, partitionKey, ct)` (partition non-null) and `StreamCrossPartitionAsync(query, parameters, ct)` are used identically in the interface (Task 2), implementation (Task 2), call-site migration (Task 3), and the regex `\.StreamCrossPartitionAsync\s*\(` (Task 4). Consistent.

**Risk note:** Making `StreamAsync`'s partition key non-nullable is a source-compatible change only if no caller relied on passing `null`; Task 2 Step 3 deliberately surfaces every such caller as a compile error, and Task 3 migrates them. No silent behavior change.
