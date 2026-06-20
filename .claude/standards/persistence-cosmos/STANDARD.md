---
name: persistence-cosmos
id-prefix: COSMOS
status: active
applies-to:
  - "src/PinballWizard.Infrastructure/Persistence/**"
  - "infra/**/*.bicep"
---

# Persistence (Cosmos) Standard

Schema CRUD via ARM; item CRUD via the data-plane SDK. Reads follow the
ADR-0036 tier model. Cosmos tuning per ADR-0025.

**RULE COSMOS-01** (arm-schema-dataplane-items)
WHEN:   provisioning Cosmos schema or performing runtime item CRUD
THEN:   schema (databases/containers/PK/throughput) goes through ARM (Azure.ResourceManager.CosmosDB); item CRUD goes through Microsoft.Azure.Cosmos
NEVER:  declare a Cosmos container in Bicep
CHECK:  rg -ni "Microsoft.DocumentDB/databaseAccounts/.*/containers|resource .* containers" infra/
SEV:    🔴
REF:    INVARIANTS#4 · ADR-0012

**RULE COSMOS-02** (read-tier-model)
WHEN:   adding a Cosmos read
THEN:   use T0 keyed read / T1 partition-aligned / T2 bounded-justified cross-partition / T3 change-feed projection; cross-partition goes through IRepository<T>.StreamCrossPartitionAsync and is listed in CrossPartitionQueryAllowListTests
NEVER:  add an ad-hoc cross-partition scan on a user-facing or unbounded-aggregate path
CHECK:  dotnet test --filter "FullyQualifiedName~CrossPartitionQueryAllowListTests" --nologo
SEV:    🔴
REF:    INVARIANTS#18 · ADR-0036

**RULE COSMOS-03** (metrics-wrapper)
WHEN:   adding a repo method that calls the Cosmos SDK
THEN:   route it through CosmosRepository<T>.ExecuteWithMetricsAsync so RU + duration land on pinwiz.cosmos.*
NEVER:  call the Cosmos SDK directly from a repo method without the metrics wrapper
CHECK:  (qualitative — /local-review) — new repo method bypassing ExecuteWithMetricsAsync
SEV:    ⚠️
REF:    INVARIANTS#13 · ADR-0025

**RULE COSMOS-04** (write-tuning)
WHEN:   adding a Container registration / write-heavy container / write path
THEN:   write-heavy container has a selective indexing policy; new container has a documented TTL decision; EnableContentResponseOnWrite=false unless the caller consumes the body; 2nd writer of a single-writer container uses ItemRequestOptions.IfMatchEtag
NEVER:  default-index a write-heavy container or re-introduce EnableContentResponseOnWrite=true without a body consumer
CHECK:  (qualitative — /local-review) — verify indexing policy, TTL decision, ETag, EnableContentResponseOnWrite against ADR-0025
SEV:    ⚠️
REF:    INVARIANTS#13 · ADR-0025

## Definition of Done

- COSMOS-01: no Cosmos container declared in Bicep (grep clean).
- COSMOS-02: CrossPartitionQueryAllowListTests passes; new cross-partition call sites allow-listed.
- COSMOS-03: new repo methods route through ExecuteWithMetricsAsync.
- COSMOS-04: indexing/TTL/ETag/EnableContentResponseOnWrite verified.
