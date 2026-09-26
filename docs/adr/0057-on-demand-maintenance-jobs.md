# 0057 — On-demand ACA jobs for corpus maintenance

**Status:** Accepted
**Date:** 2026-09-26

## Context

`--relink-all` and `--gc-rag-index` change the live catalog and the RAG index. They are operator steps after a linker change (the Iron Maiden era fix, #596, is the case that made the gap concrete): a Stern games run moves a slug, relink follows it, and GC deletes index chunks whose `(document_id, machine_id)` pair no longer has a fan-out row.

The nightly linker job runs `--download-and-link`. That verb does not reset Linked and NotInCatalog rows, so starting it does not re-home a document that is already linked to the wrong machine. No job ran `--gc-rag-index`. [ADR-0039](0039-blob-document-store.md) documented both the blob store and a laptop `dotnet run` for the one-time backfill. A laptop run needs the operator's data-plane RBAC and is not the same identity, image, or log path as the rest of the CLI fleet.

## Decision

Two manual-trigger Container Apps Jobs, defined in `infra/modules/shared.bicep` with the same module as the scheduled jobs (`deploy/scheduled-cli-job/scheduled-cli-job.bicep`):

| Job | Command | Trigger |
| --- | --- | --- |
| `pinwiz-job-relink-all-<suffix>` | `dotnet PinballWizard.Cli.dll --relink-all` | Manual |
| `pinwiz-job-gc-rag-index-<suffix>` | `dotnet PinballWizard.Cli.dll --gc-rag-index` | Manual |

They use `cliImageTag`, the shared user-assigned identity `pinwiz-aca-id-dev` (`AZURE_CLIENT_ID`), Cosmos and App Insights env, and the existing `pinwiz-job-*` failure alert. Relink also gets the linker's blob endpoint and Storage Blob Data Contributor grant, because page-1 reads come from `pinwiz-raw`. GC also gets `AiSearch:Endpoint` / `pinwiz-rag-v1` and Search Index Data Contributor, because it deletes orphan chunks. The GC job also needs the Foundry endpoint (`AiFoundry:ProjectEndpoint`, and `AiFoundry:EmbeddingDeploymentName` the same way) because dependency injection constructs `IChunkEmbedder` when the indexer is resolved, even though GC only deletes chunks. Neither job has a cron. An operator starts one execution with `az containerapp job start`. Relink completes before GC: GC only deletes pairs that no longer have a fan-out row.

The module's `triggerType` parameter defaults to `Schedule`, so the existing twenty jobs keep their crons.

## Consequences

**Positive:**

- The maintenance verbs run as the same image and identity as the rest of the CLI, and their logs land in the same Container Apps tables.
- A failed run is covered by `pinwiz-alert-aca-job-failure` because the alert already matches every `pinwiz-job-*` name.
- The deployment stack creates the jobs. There is no portal resource and no `az containerapp job create`.

**Negative:**

- The fleet is 22 jobs. The deploy workflow's "expected job count" text has to move with it; the verify step still fails closed if the list is empty or any job is on the wrong image.
- Relink holds a replica for up to two hours (`replicaTimeout` 7200). That is longer than the nightly linker because it resets the linked corpus and walks it to a fixpoint.
- `shared.bicep` is at the Bicep 64-output ceiling, so these two jobs do not add outputs. The name is the prefix plus the environment suffix, same as the scraper jobs that already omit principal-id outputs.

## Alternatives considered

- Keep the laptop `dotnet run` from ADR-0039. Rejected for this rollout: it is a different identity and it is not what the scheduled fleet uses.
- Start `pinwiz-job-linker-*` instead of a new job. Rejected: its command is `--download-and-link`, which does not reset existing links.
- Give the maintenance jobs a cron. Rejected: a full relink and an index delete are operator-gated. A nightly reset would rewrite the catalog without a reason.
- Override the linker command per execution with `az containerapp job start --command`. Rejected: that bypasses the deployment stack, and the next stack run would not record the verb.

## References

- [ADR-0039](0039-blob-document-store.md) — blob store; the local `--relink-all` backfill step now points here
- [ADR-0012](0012-cosmos-arm-schema-data-plane-items.md) — data-plane identity
- [ADR-0013](0013-two-tier-bicep-deploy.md) — deployment stacks
- #596 — 1981 Iron Maiden cited the 2018 manual until relink and GC run
