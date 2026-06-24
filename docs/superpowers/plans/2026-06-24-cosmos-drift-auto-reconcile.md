# Cosmos container drift auto-reconcile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `--ensure-cosmos-containers` reconcile an existing container's index policy + default TTL to the declared `CosmosOptions` in place (instead of only warning), in both provisioners. Then an ops run on live + emulator closes issue #494.

**Architecture:** Each provisioner already builds the desired container shape from `CosmosContainerOptions` in its create path and already has drift predicates (`IndexingPolicyMatches`, `TtlMatches`). We (a) extract the desired-shape build into an `internal static` builder shared by create + reconcile, (b) replace the WARN-on-drift branches with a reconcile call (ARM: `CreateOrUpdateAsync`; data-plane: `Container.ReplaceContainerAsync`) + INFO log. Spec: [docs/superpowers/specs/2026-06-24-cosmos-drift-auto-reconcile-design.md](../specs/2026-06-24-cosmos-drift-auto-reconcile-design.md).

**Tech Stack:** .NET 10, `Azure.ResourceManager.CosmosDB` (ARM), `Microsoft.Azure.Cosmos` (data-plane), xUnit + NSubstitute.

## Global Constraints

- **TDD where feasible.** The ARM SDK resource graph is not mockable in this repo (no precedent); test the decision logic (drift predicates) + content builder as `internal static` units, and validate the ARM `CreateOrUpdateAsync` call end-to-end via the post-merge live ops run. The data-plane reconcile IS behaviorally testable (mock `Database`/`Container`) — do so.
- **Partition key is NEVER changed by reconcile** — the builder always passes `CosmosContainerOptions.PartitionKeyPath`; a PK mismatch on an existing container still throws (unchanged). Reconcile covers index policy + default TTL only.
- **`CosmosOptions` is the single source of truth** (ADR-0012) — reconcile converges existing containers to it.
- **Commit identity:** `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no Claude attribution trailer**; conventional `type(scope) subject`; stage explicit paths (never `git add -A`).
- **No XML doc comments.** Build `dotnet build PinballWizard.slnx` 0 warn / 0 err; pre-push `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"` green.
- **Branch:** `feat/cosmos-drift-reconcile` (created; spec committed there).
- `Infrastructure.Tests` already has `InternalsVisibleTo` (it uses internal Cosmos types) — `internal static` helpers are testable directly.

---

### Task 1: ARM provisioner — reconcile index + TTL drift in place

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ArmCosmosProvisioner.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/ArmCosmosProvisionerTests.cs` (new)

**Interfaces:**
- Produces: `internal static CosmosDBSqlContainerCreateOrUpdateContent BuildContainerContent(CosmosContainerOptions)`; the existing `IndexingPolicyMatches` / `TtlMatches` become `internal static`.

- [ ] **Step 1: Write the failing tests** (builder + predicates as units)

```csharp
using Azure.ResourceManager.CosmosDB.Models;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

public sealed class ArmCosmosProvisionerTests
{
    [Fact]
    public void BuildContainerContent_CarriesPartitionKey_Ttl_AndIndexPaths()
    {
        var opts = new CosmosContainerOptions
        {
            Name = "scraped_documents_raw",
            PartitionKeyPath = "/document_id",
            DefaultTtlSeconds = null,
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/document_id/?", "/run_id/?"],
                ExcludedPaths = ["/*"],
            },
        };

        var content = ArmCosmosProvisioner.BuildContainerContent(opts);

        Assert.Equal("/document_id", content.Resource.PartitionKey.Paths[0]);
        Assert.Equal(CosmosDBPartitionKind.Hash, content.Resource.PartitionKey.Kind);
        Assert.Contains("/run_id/?", content.Resource.IndexingPolicy.IncludedPaths.Select(p => p.Path));
        Assert.Contains("/*", content.Resource.IndexingPolicy.ExcludedPaths.Select(p => p.Path));
        Assert.Null(content.Resource.DefaultTtl);
    }

    [Fact]
    public void IndexingPolicyMatches_FalseWhenIncludedPathsDiffer()
    {
        var actual = new CosmosDBIndexingPolicy();
        actual.IncludedPaths.Add(new CosmosDBIncludedPath { Path = "/document_id/?" });
        actual.ExcludedPaths.Add(new CosmosDBExcludedPath { Path = "/*" });
        var expected = new CosmosIndexingPolicyOptions
        {
            IncludedPaths = ["/document_id/?", "/run_id/?"],
            ExcludedPaths = ["/*"],
        };

        Assert.False(ArmCosmosProvisioner.IndexingPolicyMatches(actual, expected));
    }

    [Fact]
    public void TtlMatches_TrueOnlyOnExactNullableEquality()
    {
        Assert.True(ArmCosmosProvisioner.TtlMatches(null, null));
        Assert.False(ArmCosmosProvisioner.TtlMatches(-2, null));
        Assert.True(ArmCosmosProvisioner.TtlMatches(7776000, 7776000));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~ArmCosmosProvisionerTests"`
Expected: FAIL — `BuildContainerContent` doesn't exist; the predicates are `private`.

- [ ] **Step 3: Extract the builder + widen the predicates**

In `ArmCosmosProvisioner.cs`, extract the create path's resource/content construction (current lines ~192-211) into:

```csharp
internal static CosmosDBSqlContainerCreateOrUpdateContent BuildContainerContent(CosmosContainerOptions containerOptions)
{
    var resource = new CosmosDBSqlContainerResourceInfo(containerOptions.Name)
    {
        PartitionKey = new CosmosDBContainerPartitionKey
        {
            Paths = { containerOptions.PartitionKeyPath },
            Kind = CosmosDBPartitionKind.Hash,
        },
    };
    if (containerOptions.DefaultTtlSeconds is { } ttl)
    {
        resource.DefaultTtl = ttl;
    }
    if (containerOptions.IndexingPolicy is { } indexingPolicy)
    {
        resource.IndexingPolicy = BuildArmIndexingPolicy(indexingPolicy);
    }
    return new CosmosDBSqlContainerCreateOrUpdateContent(AzureLocation.EastUS2, resource);
}
```

Change `private static bool IndexingPolicyMatches(...)` and `private static bool TtlMatches(...)` to `internal static`. In the create path, replace the inline build + `new CosmosDBSqlContainerCreateOrUpdateContent(...)` with `var content = BuildContainerContent(containerOptions);` (keep the existing `CreateOrUpdateAsync` call + the "created via ARM" INFO log unchanged).

- [ ] **Step 4: Run tests to verify they pass**

Run: the Step-2 filter. Expected: PASS.

- [ ] **Step 5: Replace the WARN-on-drift branches with a reconcile**

In `EnsureContainerAsync`'s `existing is not null` block, replace the two `_logger.LogWarning(...)` drift branches (index ~161-167, TTL ~175-183) and the trailing "already present" return with:

```csharp
            var indexDrift = containerOptions.IndexingPolicy is { } expected
                && !IndexingPolicyMatches(existing.Data.Resource?.IndexingPolicy, expected);
            var ttlDrift = !TtlMatches(existing.Data.Resource?.DefaultTtl, containerOptions.DefaultTtlSeconds);

            if (indexDrift || ttlDrift)
            {
                await containers.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    containerOptions.Name,
                    BuildContainerContent(containerOptions),
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Container '{Container}' reconciled via ARM to match configuration ({What}).",
                    containerOptions.Name,
                    indexDrift && ttlDrift ? "index policy + default TTL" : indexDrift ? "index policy" : "default TTL");
                return;
            }

            _logger.LogInformation(
                "Container '{Container}' already present via ARM (partition key {PartitionKeyPath}).",
                containerOptions.Name,
                containerOptions.PartitionKeyPath);
            return;
```

(The partition-key-mismatch `throw` above this stays unchanged — PK is never reconciled.)

- [ ] **Step 6: Build + run the focused tests + the existing Cosmos suites**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~ArmCosmosProvisionerTests|FullyQualifiedName~CosmosProvisionerSelectionTests|FullyQualifiedName~CosmosOptionsTests"`
Expected: build 0/0; PASS. (The reconcile `CreateOrUpdateAsync` call itself is validated by the post-merge live ops run — ARM resource-graph mocking is not established in this repo; note this in the report.)

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Infrastructure/Persistence/Cosmos/ArmCosmosProvisioner.cs tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/ArmCosmosProvisionerTests.cs
git commit -m "feat(persistence) ArmCosmosProvisioner reconciles index+TTL drift in place"
```

---

### Task 2: Data-plane provisioner — reconcile via ReplaceContainerAsync

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/DataPlaneCosmosProvisioner.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/DataPlaneCosmosProvisionerTests.cs` (new)

**Interfaces:**
- Produces: `internal static ContainerProperties BuildContainerProperties(CosmosContainerOptions)`; `IndexingPolicyMatches` / `TtlMatches` become `internal static`.

- [ ] **Step 1: Write the failing tests** (builder unit + behavioral reconcile)

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

public sealed class DataPlaneCosmosProvisionerTests
{
    [Fact]
    public void BuildContainerProperties_CarriesPartitionKey_Ttl_AndIndexPaths()
    {
        var opts = new CosmosContainerOptions
        {
            Name = "scraped_documents_raw",
            PartitionKeyPath = "/document_id",
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/document_id/?", "/run_id/?"],
                ExcludedPaths = ["/*"],
            },
        };

        var props = DataPlaneCosmosProvisioner.BuildContainerProperties(opts);

        Assert.Equal("/document_id", props.PartitionKeyPath);
        Assert.Contains("/run_id/?", props.IndexingPolicy.IncludedPaths.Select(p => p.Path));
    }

    [Fact]
    public async Task EnsureContainer_ReplacesWhenIndexDrifts()
    {
        // existing container returned by CreateContainerIfNotExists has an OUTDATED index policy
        var drifted = new ContainerProperties("scraped_documents_raw", "/document_id");
        drifted.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/document_id/?" });
        drifted.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });

        var (provisioner, database, container) = ArrangeProvisioner(drifted);

        await provisioner.EnsureDatabaseAndContainersAsync("pinwiz", [ScrapedDocsOpts()], CancellationToken.None);

        await container.Received(1).ReplaceContainerAsync(
            Arg.Is<ContainerProperties>(p => p.IndexingPolicy.IncludedPaths.Any(x => x.Path == "/run_id/?")),
            Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureContainer_DoesNotReplaceWhenMatching()
    {
        var matching = DataPlaneCosmosProvisioner.BuildContainerProperties(ScrapedDocsOpts());
        var (provisioner, _, container) = ArrangeProvisioner(matching);

        await provisioner.EnsureDatabaseAndContainersAsync("pinwiz", [ScrapedDocsOpts()], CancellationToken.None);

        await container.DidNotReceive().ReplaceContainerAsync(
            Arg.Any<ContainerProperties>(), Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>());
    }

    private static CosmosContainerOptions ScrapedDocsOpts() => new()
    {
        Name = "scraped_documents_raw",
        PartitionKeyPath = "/document_id",
        IndexingPolicy = new CosmosIndexingPolicyOptions
        {
            IncludedPaths = ["/document_id/?", "/run_id/?"],
            ExcludedPaths = ["/*"],
        },
    };

    private static (DataPlaneCosmosProvisioner, Database, Container) ArrangeProvisioner(ContainerProperties existing)
    {
        var client = Substitute.For<CosmosClient>();
        var database = Substitute.For<Database>();
        var container = Substitute.For<Container>();

        var dbResponse = Substitute.For<DatabaseResponse>();
        dbResponse.Database.Returns(database);
        client.CreateDatabaseIfNotExistsAsync(Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>()).Returns(dbResponse);

        var containerResponse = Substitute.For<ContainerResponse>();
        containerResponse.Resource.Returns(existing);
        database.CreateContainerIfNotExistsAsync(Arg.Any<ContainerProperties>(), Arg.Any<int?>(),
            Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>()).Returns(containerResponse);
        database.GetContainer("scraped_documents_raw").Returns(container);
        container.ReplaceContainerAsync(Arg.Any<ContainerProperties>(),
            Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>()).Returns(containerResponse);

        var provisioner = new DataPlaneCosmosProvisioner(client, NullLogger<DataPlaneCosmosProvisioner>.Instance);
        return (provisioner, database, container);
    }
}
```

> The mocked-method overloads (`CreateDatabaseIfNotExistsAsync`, `CreateContainerIfNotExistsAsync`, `ReplaceContainerAsync`) must match the real SDK signatures the substitute exposes — if NSubstitute can't override a non-virtual overload, fall back to the widest virtual overload the SDK provides and adjust `Arg.Any<...>()` types to match. Verify the exact overloads at implementation time; the existing `CosmosHealthCheckTests` confirms `CosmosClient`/`Container` are substitutable in this project.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~DataPlaneCosmosProvisionerTests"`
Expected: FAIL — `BuildContainerProperties` doesn't exist; no reconcile (ReplaceContainerAsync never called).

- [ ] **Step 3: Extract the builder + widen predicates**

In `DataPlaneCosmosProvisioner.cs`, extract the properties construction (current lines ~59-67) into:

```csharp
internal static ContainerProperties BuildContainerProperties(CosmosContainerOptions containerOptions)
{
    var properties = new ContainerProperties(containerOptions.Name, containerOptions.PartitionKeyPath);
    if (containerOptions.DefaultTtlSeconds is { } ttl)
    {
        properties.DefaultTimeToLive = ttl;
    }
    if (containerOptions.IndexingPolicy is { } indexingPolicy)
    {
        ApplyIndexingPolicy(properties.IndexingPolicy, indexingPolicy);
    }
    return properties;
}
```

Use it at the top of `EnsureContainerAsync` (`var properties = BuildContainerProperties(containerOptions);`). Change `IndexingPolicyMatches` / `TtlMatches` to `internal static`.

- [ ] **Step 4: Replace the WARN-on-drift branches with a reconcile**

In `EnsureContainerAsync`, after the partition-key mismatch `throw`, replace the two `_logger.LogWarning(...)` drift branches with:

```csharp
        var indexDrift = containerOptions.IndexingPolicy is { } expectedPolicy
            && !IndexingPolicyMatches(response.Resource.IndexingPolicy, expectedPolicy);
        var ttlDrift = !TtlMatches(response.Resource.DefaultTimeToLive, containerOptions.DefaultTtlSeconds);

        if (indexDrift || ttlDrift)
        {
            await database.GetContainer(containerOptions.Name)
                .ReplaceContainerAsync(properties, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Container '{Container}' reconciled via data-plane to match configuration ({What}).",
                containerOptions.Name,
                indexDrift && ttlDrift ? "index policy + default TTL" : indexDrift ? "index policy" : "default TTL");
            return;
        }

        _logger.LogInformation(
            "Container '{Container}' ready via data-plane SDK (partition key {PartitionKeyPath}, default TTL {Ttl}, indexing {Indexing}).",
            containerOptions.Name,
            containerOptions.PartitionKeyPath,
            containerOptions.DefaultTtlSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            containerOptions.IndexingPolicy is null ? "default" : "selective");
```

- [ ] **Step 5: Run tests to verify they pass**

Run: the Step-2 filter. Expected: PASS (builder + both behavioral tests).

- [ ] **Step 6: Build + Cosmos suites**

Run: `dotnet build PinballWizard.slnx` then `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~DataPlaneCosmosProvisionerTests|FullyQualifiedName~CosmosProvisionerSelectionTests"`
Expected: build 0/0; PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Infrastructure/Persistence/Cosmos/DataPlaneCosmosProvisioner.cs tests/PinballWizard.Infrastructure.Tests/Persistence/Cosmos/DataPlaneCosmosProvisionerTests.cs
git commit -m "feat(persistence) DataPlaneCosmosProvisioner reconciles index+TTL drift via ReplaceContainerAsync"
```

---

### Task 3: ADR-0012 amendment — ensure is the canonical reconciler

**Files:**
- Modify: `docs/adr/0012-cosmos-arm-schema-data-plane-items.md`

- [ ] **Step 1: Append a dated amendment** at the end of the ADR:

```markdown
## Amendment (2026-06-24): ensure reconciles drift in place

`--ensure-cosmos-containers` is now the canonical **reconciler**, not only the creator. When an
existing container's **index policy** or **default TTL** differs from the declared `CosmosOptions`,
both provisioners update it in place — `ArmCosmosProvisioner` via `CreateOrUpdateAsync`,
`DataPlaneCosmosProvisioner` via `Container.ReplaceContainerAsync` — and log at INFO. Index-policy
updates are non-destructive (Cosmos re-indexes in the background); default-TTL changes are
forward-only. **Partition key and throughput remain create-only** — a partition-key mismatch on an
existing container still throws (it is a recreate, out of scope). Closes the drift surfaced in
issue #494.
```

- [ ] **Step 2: Verify markdown renders + no lint regressions introduced**, then commit

```bash
git add docs/adr/0012-cosmos-arm-schema-data-plane-items.md
git commit -m "docs(adr) ADR-0012 amendment: ensure reconciles index+TTL drift in place"
```

---

## Post-merge ops step (NOT a code task — the controller runs this after the PR merges)

Run `--ensure-cosmos-containers` to apply the reconcile and close #494:

1. **Live** (isolated personal config, exactly as the 2026-06-24 `scrape_runs` ensure):
   ```powershell
   $env:AZURE_CONFIG_DIR = "c:\earlybird\PinballWizard\.azure-local"   # personal pinwiz.ai identity
   $env:Cosmos__AccountEndpoint   = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
   $env:Cosmos__AccountResourceId = "/subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev-buutj"
   $env:DOTNET_ENVIRONMENT        = "Development"
   dotnet run --project src/PinballWizard.Cli --no-launch-profile -- --ensure-cosmos-containers
   ```
   Expect INFO "reconciled" lines for the five drifted containers + `scraped_documents_raw` (now index-backed for `run_id`).
2. **Emulator** (AppHost up; `ConnectionStrings__cosmos` from the dashboard, as before): same verb; expect the TTL-drift containers to log "reconciled".

## Self-Review

**Spec coverage:** §2.1 reconcile both provisioners → Tasks 1+2. §2.2 PK never touched → asserted (build passes PartitionKeyPath; PK-mismatch throw unchanged) in both tasks. §2.3 safety → in-place update calls (no delete). §2.4 idempotency → "DoesNotReplaceWhenMatching" test (data-plane) + the `if (indexDrift || ttlDrift)` gate (ARM). §2.5 ops → post-merge step. ADR → Task 3.

**Placeholder scan:** none. The one soft note (data-plane mock overload matching) names the concrete fallback and the existing precedent (`CosmosHealthCheckTests`).

**Type consistency:** `BuildContainerContent` (ARM) / `BuildContainerProperties` (data-plane), `IndexingPolicyMatches` / `TtlMatches` widened to `internal static`, used identically across each task's create + reconcile paths.

**Known coverage gap (honest):** the ARM `CreateOrUpdateAsync` reconcile *call* is not unit-tested (ARM resource-graph mocking is not established in this repo and is brittle); it is covered by the builder + predicate unit tests plus the post-merge live ops run. The data-plane reconcile *is* behaviorally unit-tested.
