---
title: "Cosmos container drift auto-reconcile (ensure = canonical reconciler)"
date: 2026-06-24
status: accepted
related:
  - docs/adr/0012-cosmos-arm-schema-data-plane-items.md   # schema via ARM, items via data-plane; "runtime ensure is canonical creator"
  - https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/494   # the drift this closes
  - docs/superpowers/specs/2026-06-24-recent-documents-per-run-drilldown-design.md   # added /run_id/? to scraped_documents_raw index (needs re-apply)
---

# Cosmos container drift auto-reconcile

## 1. Problem & intent

`--ensure-cosmos-containers` is the canonical container creator (ADR-0012 — containers are not in
Bicep). But it is **create-if-missing only**: for an existing container whose **index policy** or
**default TTL** no longer matches the declared `CosmosOptions`, both provisioners merely **log a
warning** and tell the operator to recreate the container or hand-edit it via Data Explorer. Running
the verb on 2026-06-24 surfaced this (issue #494): five live containers show index-policy drift, the
emulator shows default-TTL drift on most containers, and the `/run_id/?` index added to
`scraped_documents_raw` (drill-down feature) will **never** apply to the existing live/emulator
container because the provisioner won't re-apply it.

A manual portal/CLI re-apply is the ad-hoc workaround the showcase bar explicitly rejects, and the
drift recurs on the next container whose config changes. **Intent:** make `ensure` converge every
existing container's index policy + default TTL to `CosmosOptions`, **in place**, so "ensure means
ensure" — the verb is the canonical *reconciler*, not just creator.

## 2. Design

### 2.1 Reconcile on drift instead of warning (both provisioners)

When `EnsureCreatedAsync` finds an **existing** container and detects that its index policy or
default TTL differs from the configured `ContainerOptions`, it **updates the container in place** to
match config, then logs `INFO "reconciled"` (replacing today's `WARN "differs — re-apply
manually"`).

- **`ArmCosmosProvisioner`** (deployed Cosmos, AAD): the create path already builds the desired
  resource (`CosmosDBSqlContainerCreateOrUpdateContent` with `BuildArmIndexingPolicy(...)` +
  `DefaultTtl`) and calls `CreateOrUpdate`. That same call is an **update** when the container
  exists. On detected drift, build the desired container resource **from `CosmosOptions`** (same
  partition key, desired index policy, desired TTL) and issue the create-or-update.
- **`DataPlaneCosmosProvisioner`** (Aspire emulator, master key): on detected drift, build a
  `ContainerProperties` from `CosmosOptions` (id, partition key path, `IndexingPolicy`,
  `DefaultTimeToLive`) and call `Container.ReplaceContainerAsync(properties, ...)`.

### 2.2 What is reconciled — and what is never touched

- **Reconciled:** index policy (included/excluded paths, the selective-vs-default shape) and default
  TTL.
- **Never touched:** **partition key** (immutable in Cosmos — the reconcile passes the *same*
  `PartitionKeyPath` from config, so it is structurally incapable of changing it) and **throughput**
  (out of scope). If a future change required a partition-key change, that is a recreate, explicitly
  out of scope here and still surfaced as a warning.

### 2.3 Safety (why in-place update is non-destructive)

- **Index policy updates are non-destructive.** Cosmos applies a new indexing policy by re-indexing
  in the **background**; the `CreateOrUpdate`/`ReplaceContainerAsync` call returns promptly and
  queries keep working (un-indexed paths fall back to scan until the re-index completes — the same
  graceful degradation the drill-down spec already documents). **No data is dropped.**
- **Default-TTL updates are forward-only** metadata changes — they change expiry semantics going
  forward, never delete existing items synchronously.
- **No data loss path exists** in this change: we only ever issue index/TTL updates on a container
  whose id + partition key already match config.

### 2.4 Idempotency & logging

- A container already matching config takes the existing "already present" path — **no update
  call** is issued (the drift predicate is the gate). Re-running `ensure` on a converged account is
  a no-op beyond the existence checks.
- Drift → `INFO "Container '{Container}' reconciled via {ARM|data-plane} to match configuration
  (index policy / default TTL)."` The misleading "re-apply by recreating … via Data Explorer"
  warnings are removed.

### 2.5 Ops step (after merge)

Run `--ensure-cosmos-containers` against **live** (isolated personal `AZURE_CONFIG_DIR` →
`pinwiz.ai`, `Cosmos__AccountEndpoint` + `Cosmos__AccountResourceId`, exactly as the 2026-06-24
`scrape_runs` ensure) and the **emulator** (Aspire AppHost up, `ConnectionStrings__cosmos`). This
applies `/run_id/?` to the existing `scraped_documents_raw`, clears the five-container index drift,
and clears the emulator default-TTL drift — closing #494.

## 3. Components touched

- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ArmCosmosProvisioner.cs` — replace the
  index-drift + TTL-drift WARN branches with a reconcile (issue the create-or-update with the desired
  resource); INFO log. Extract the desired-resource build so create and reconcile share it.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/DataPlaneCosmosProvisioner.cs` —
  replace its drift WARN branch(es) with `ReplaceContainerAsync` from a config-built
  `ContainerProperties`; INFO log.
- Modify: `docs/adr/0012-cosmos-arm-schema-data-plane-items.md` — append a dated amendment: `ensure`
  is the canonical **reconciler** (index + default TTL converge in place; partition key/throughput
  remain create-only / out of scope).
- Tests (see §4).

## 4. Testing

Both provisioners already have unit tests that mock the ARM SDK / `Container`. Extend them:

- **`ArmCosmosProvisioner`:**
  - Existing container with **index-policy drift** ⇒ a create-or-update **is** issued whose content
    carries the desired (config) index policy + the unchanged partition key. Assert the issued
    content, and that the log is INFO "reconciled" (not WARN).
  - Existing container with **TTL drift** ⇒ create-or-update issued with the desired `DefaultTtl`.
  - Existing container **matching** config ⇒ **no** create-or-update issued (only the existence
    read).
- **`DataPlaneCosmosProvisioner`:**
  - Index/TTL drift ⇒ `ReplaceContainerAsync` called with `ContainerProperties` carrying the desired
    index policy / `DefaultTimeToLive` and the same partition key path.
  - Matching ⇒ `ReplaceContainerAsync` **not** called.
- Build `-warnaserror` 0/0; the existing `CosmosOptionsTests` / provisioner suites stay green.

## 5. Non-goals / YAGNI

- **Partition-key or throughput reconcile** — PK is immutable (recreate territory); throughput is
  out of scope. Both remain create-only; a PK mismatch still warns.
- **Recreate-on-incompatible-change** — index + TTL are updatable in place, so no destructive
  recreate path is added.
- **A reconcile dry-run / diff report** — `ensure` converges; if a preview is ever wanted it is a
  separate verb. (The INFO logs already name what was reconciled.)
- **Reconciling containers not declared in `CosmosOptions`** — `ensure` only governs declared
  containers; it never inspects or touches others.

## 6. Risks

- **Unexpected reconcile of a deliberately-divergent container.** `CosmosOptions` is the single
  source of truth (ADR-0012), so converging to it is correct by definition; there is no supported
  reason for a declared container to diverge. Mitigated by the change being scoped to declared
  containers only.
- **Re-index cost on a large container.** Re-indexing `scraped_documents_raw` consumes RU in the
  background. Bounded (one selective path added); acceptable on the dev account; the call itself is
  non-blocking.
- **SDK-shape correctness** (ARM `CosmosDBSqlContainerCreateOrUpdateContent` vs data-plane
  `ContainerProperties`/`ReplaceContainerAsync`). Mitigated by reusing the providers' existing
  create/build code paths and by the per-provisioner tests asserting the issued shape.
