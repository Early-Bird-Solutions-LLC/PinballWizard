# 0012 — Cosmos schema CRUD via ARM, item CRUD via data-plane SDK

**Status:** Accepted
**Date:** 2026-05-04

## Context

Cosmos DB exposes its functionality through two distinct .NET SDK surfaces:

- **Data-plane SDK** ([`Microsoft.Azure.Cosmos`](https://www.nuget.org/packages/Microsoft.Azure.Cosmos)) — the runtime client. Reads, writes, queries documents inside containers. Also exposes convenience methods for creating databases and containers (`CosmosClient.CreateDatabaseIfNotExistsAsync`, `Database.CreateContainerIfNotExistsAsync`), so it is naturally tempting to use it for everything.
- **ARM SDK** ([`Azure.ResourceManager.CosmosDB`](https://www.nuget.org/packages/Azure.ResourceManager.CosmosDB)) — the control-plane client. Manages the Cosmos account itself plus its child resources (databases, containers, throughput, RBAC role assignments).

The system has two distinct needs that map onto these two surfaces:

1. **Schema bootstrap at startup.** The runtime app needs to verify that its expected database and containers exist (with the correct partition keys), and create them if they don't. This must work both against the deployed Cosmos account (using AAD authentication via `DefaultAzureCredential`) and against the local Aspire-orchestrated emulator (which only supports master-key authentication).
2. **Document CRUD at runtime.** Items (machines, ingestion-source records, scraped documents) read and written constantly while the app runs.

The original design assumed both could go through the data-plane SDK with a custom Cosmos data-plane RBAC role granting the runtime principal schema-mutation permissions. PR #62 attempted this with a role definition including the action set `Microsoft.DocumentDB/databaseAccounts/sqlDatabases/*`. Azure rejected the role definition at deploy-time validation: the wildcard action set is not a valid SQL data action. **Cosmos data-plane RBAC genuinely does not model schema-mutation actions** — this is fundamental to the service's design, not a configuration limit a custom role can work around.

The deploy from PR #62 failed mid-flight, so the broken role never applied, but the PR was already merged with non-functional Bicep. The lesson cost a redesign cycle (PR #63) and is worth a permanent record so the same path is not retried by future contributors (or future-Claude).

## Decision

**Cosmos schema operations go through the ARM SDK. Cosmos item operations go through the data-plane SDK.** An `ICosmosProvisioner` abstraction selects between two implementations based on configuration.

### Implementation split

| Operation class | SDK | Concrete type |
| --- | --- | --- |
| Database create / replace / read / delete | `Azure.ResourceManager.CosmosDB` | `ArmCosmosProvisioner` |
| Container create / replace / read / delete | `Azure.ResourceManager.CosmosDB` | `ArmCosmosProvisioner` |
| Partition-key drift detection | `Azure.ResourceManager.CosmosDB` | `ArmCosmosProvisioner` |
| Throughput changes | `Azure.ResourceManager.CosmosDB` (when needed) | `ArmCosmosProvisioner` |
| Document CRUD (item read/write/upsert/query) | `Microsoft.Azure.Cosmos` | `MachineRepository`, `IngestionSourceRepository`, etc. |

### Provisioner selection

`AddCosmosPersistence` registers `ICosmosProvisioner` based on whether `Cosmos:AccountResourceId` is configured:

- **`AccountResourceId` set** → register `ArmCosmosProvisioner`. Used against deployed Cosmos with AAD authentication via `DefaultAzureCredential`.
- **`AccountResourceId` unset** → register `DataPlaneCosmosProvisioner`. Used against the local Aspire Cosmos preview emulator, which authenticates with the master key from `ConnectionStrings:cosmos` and supports schema CRUD through the data-plane SDK.

The runtime exposes `--ensure-cosmos-containers` as the canonical post-deploy smoke-test. It resolves `ICosmosProvisioner` from DI and runs `EnsureCreatedAsync`, which creates the configured database + every container in `CosmosOptions.Containers` if missing and verifies existing partition-key paths match. Idempotent. Returns exit code 2 with a remediation message when Cosmos isn't configured.

### Operational consequences

The deployed runtime principal (Managed Identity, in production; signed-in developer, in dev) needs **two role assignments — one in each of Cosmos's two independent RBAC systems**:

| Role | RBAC system | Why |
| --- | --- | --- |
| `Cosmos DB Operator` | Azure RBAC (account scope) | `ArmCosmosProvisioner` creates / replaces / reads databases and containers via ARM. Built-in Azure role granting `Microsoft.DocumentDb/databaseAccounts/*`. |
| `Cosmos DB Built-in Data Contributor` | Cosmos SQL RBAC (account scope) | `MachineRepository.UpsertAsync` and other item-CRUD paths against the data-plane SDK. Cosmos-specific role definition (well-known ID `00000000-0000-0000-0000-000000000002`); not visible via `az role definition list`. |

The two RBAC systems are independent. Subscription Owner inheritance covers Azure RBAC (so `Cosmos DB Operator`-equivalent scope is implicit for the dev) but **does NOT** automatically grant Cosmos SQL RBAC, which must be explicitly assigned at the Cosmos account scope via `Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments`. Bicep grants the data-plane role to the dev principal (per [PR #60](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/60)); production Managed Identity needs the data-plane role assigned explicitly, plus an Azure RBAC role assignment for the ARM operations (either `Cosmos DB Operator` directly or a broader role that subsumes it).

## Consequences

**Positive:**

- Clean separation of concerns: deploy creates the Cosmos account (Bicep / ARM), runtime ensures its own schema (ARM SDK), runtime reads / writes items (data-plane SDK).
- AAD authentication throughout the deployed environment — no master keys leaving the Cosmos account.
- The Aspire emulator path stays simple. The emulator does not support AAD; `DataPlaneCosmosProvisioner` uses the master-key connection string Aspire injects, which lets the same `ICosmosProvisioner` interface drive both environments.
- Partition-key drift detection has a clear home (in `ArmCosmosProvisioner`) — the runtime can fail loudly if the deployed container's partition key drifts from what the code expects.
- Cosmos containers stay out of Bicep. Container existence and configuration are runtime concerns; the runtime owns verification and remediation. This keeps the deploy principal focused on control-plane operations and avoids conflating control-plane and data-plane permissions in the deploy identity.

**Negative:**

- Two SDKs to maintain familiarity with. Their error shapes, retry semantics, and async patterns differ in subtle ways.
- Two role assignments instead of one for the runtime principal. Slightly more Bicep surface; minor operational overhead.
- The selection key (`Cosmos:AccountResourceId` set vs. unset) is implicit. A misconfigured deployment that omits `AccountResourceId` would silently fall back to data-plane provisioning, which fails against deployed Cosmos. Mitigation: `ArmCosmosProvisioner.EnsureCreatedAsync` includes a friendly-error guard validating the resource ID format and producing a clear remediation message when malformed (the Git-Bash MSYS path-translation case it was designed for).

## Alternatives considered

- **Data-plane SDK for everything, master-key authentication.** Rejected because master-key authentication violates the AAD-everywhere principle in deployed environments, and master keys are an operational risk: rotation pain, leak blast radius, no per-principal audit trail. Acceptable only for the Aspire emulator (which does not support AAD).
- **Data-plane SDK for everything, custom RBAC role granting schema actions.** *Attempted in [PR #62](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/62) and rejected by Azure runtime validation.* The role definition `Microsoft.DocumentDB/databaseAccounts/sqlDatabases/*` is not a valid SQL data action. Cosmos data-plane RBAC does not model schema-mutation actions, and no custom role can extend it to do so. The deploy script failed mid-flight and the broken Bicep landed but was non-functional; superseded by [PR #63](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/63) (this ADR's locked solution).
- **Cosmos containers declared in Bicep.** Rejected for three reasons: (1) container existence and configuration are runtime concerns the app should verify and remediate at startup, not at infrastructure-deploy time; (2) partition-key drift detection is much easier when the runtime owns container shape; (3) creating containers in Bicep means the deploy principal needs Cosmos data-plane permissions, conflating control-plane and data-plane responsibilities in the deploy identity.
- **Two separate provisioners with no abstraction (caller picks).** Rejected because the dev-vs-deployed distinction would leak into every consumer, multiplying conditional logic. The `ICosmosProvisioner` abstraction with a single DI selection point keeps the consumer code uniform.

## References

- [PR #62](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/62) — the failed custom-role attempt (non-functional Bicep landed; superseded)
- [PR #63](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/63) — the locked solution introducing `ICosmosProvisioner` + `ArmCosmosProvisioner` + `DataPlaneCosmosProvisioner`
- [PR #57](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/57) — the `--ensure-cosmos-containers` CLI flag that exercises this abstraction end-to-end
- [PR #60](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/60) — Bicep grants `Cosmos DB Built-in Data Contributor` to the developer principal at account scope
- [`docs/build-spec.md`](../build-spec.md) Phase 2 § Scope item 1 — the scope entry that specified this ADR
- [`docs/guardrails.md`](../guardrails.md) § "Locked decisions" — references this ADR as the canonical home for the rule

## Amendment (2026-06-24): ensure reconciles drift in place

`--ensure-cosmos-containers` is now the canonical **reconciler**, not only the creator. When an
existing container's **index policy** or **default TTL** differs from the declared `CosmosOptions`,
both provisioners update it in place — `ArmCosmosProvisioner` via `CreateOrUpdateAsync`,
`DataPlaneCosmosProvisioner` via `Container.ReplaceContainerAsync` — and log at INFO. Index-policy
updates are non-destructive (Cosmos re-indexes in the background); default-TTL changes are
forward-only. **Partition key and throughput remain create-only** — a partition-key mismatch on an
existing container still throws (it is a recreate, out of scope). Closes the drift surfaced in
issue #494.
