using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Strongly-typed configuration for the Cosmos DB client. Bound from
/// <c>appsettings.json</c> section <c>"Cosmos"</c> via
/// <see cref="ServiceCollectionExtensions.AddCosmosPersistence"/>.
/// </summary>
public sealed class CosmosOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Cosmos";

    /// <summary>
    /// Full configuration key for <see cref="AccountEndpoint"/>. Exposed
    /// so callers (e.g., Aspire-vs-Managed-Identity gating logic in CLI
    /// hosts) can presence-check the key without duplicating the
    /// <c>"Cosmos:AccountEndpoint"</c> string and risking a silent
    /// drift if the section is ever renamed.
    /// </summary>
    public const string AccountEndpointKey = $"{SectionName}:{nameof(AccountEndpoint)}";

    /// <summary>
    /// Connection-string name Aspire uses when wiring a Cosmos resource
    /// via <c>builder.AddAzureCosmosDB("cosmos")</c>. CLI hosts use
    /// <c>IConfiguration.GetConnectionString(CosmosConnectionName)</c>
    /// to detect Aspire-injected connections.
    /// </summary>
    public const string CosmosConnectionName = "cosmos";

    /// <summary>
    /// Full configuration key for <see cref="AccountResourceId"/>. Exposed
    /// alongside <see cref="AccountEndpointKey"/> so CLI hosts can
    /// presence-check both without duplicating the section strings.
    /// </summary>
    public const string AccountResourceIdKey = $"{SectionName}:{nameof(AccountResourceId)}";

    /// <summary>
    /// Cosmos account endpoint URL — sourced from the Bicep output
    /// <c>cosmosAccountEndpoint</c> when running against a deployed
    /// Cosmos account. Optional when an <see cref="Microsoft.Azure.Cosmos.CosmosClient"/>
    /// is already registered in DI by an external integration (e.g.,
    /// .NET Aspire's <c>AddAzureCosmosClient("cosmos")</c>, which
    /// builds the client from the Aspire-injected connection string
    /// and supersedes this endpoint).
    /// </summary>
    [Url]
    public string? AccountEndpoint { get; init; }

    /// <summary>
    /// ARM resource ID of the Cosmos account, sourced from the Bicep
    /// output <c>cosmosAccountResourceId</c>. Required when
    /// <see cref="CosmosBootstrapper"/> runs against deployed Cosmos
    /// because schema bootstrap (database / container CRUD) goes
    /// through the ARM SDK — Cosmos's data-plane RBAC does not grant
    /// schema-mutation actions, regardless of role definition.
    /// Format:
    /// <c>/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.DocumentDB/databaseAccounts/{name}</c>.
    /// Leave null when running against the Aspire preview emulator —
    /// the emulator authenticates the SDK with the master key in the
    /// connection string, which permits data-plane schema CRUD without
    /// any ARM round-trip.
    /// </summary>
    public string? AccountResourceId { get; init; }

    /// <summary>
    /// Database name within the account. Defaults to <c>pinwiz</c>;
    /// per-environment naming should distinguish dev / prod when needed.
    /// </summary>
    [Required]
    public string DatabaseName { get; init; } = "pinwiz";

    /// <summary>
    /// Container name -> partition key path mapping. The bootstrapper
    /// uses this list to ensure each container exists with the correct
    /// partition key. Defaults match the canonical Phase 1 container
    /// names the repositories already reference
    /// (<see cref="MachineRepository"/> uses <c>machines</c> with
    /// partition key <c>/manufacturer</c> per ADR 0011;
    /// <see cref="IngestionSourceRepository"/> uses
    /// <c>ingestion_sources</c> with partition key
    /// <c>/partitionKey</c>). Configuration binding REPLACES the list
    /// (does not merge), so adding a <c>Cosmos:Containers</c> entry to
    /// configuration overrides these defaults entirely.
    /// </summary>
    public IReadOnlyList<CosmosContainerOptions> Containers { get; init; } =
    [
        new() { Name = "machines", PartitionKeyPath = "/manufacturer" },
        new() { Name = "ingestion_sources", PartitionKeyPath = "/partitionKey" },
        // Title→OPDB-ID materialized view per ADR-0025 § 4. The Wizard's
        // `getMachineByTitle` Foundry function tool point-reads this
        // container first; the previous cross-partition `STRINGEQUALS`
        // query in `MachineRepository.QueryByTitleAsync` survives as a
        // logged-warning fallback. Single writer is `OpdbSyncService`,
        // which dual-writes (machine first, then lookup) under Cosmos
        // session consistency for read-your-writes. Doc id equals the
        // partition-key value (the normalized title) so reads are pure
        // point lookups with no secondary index. Selective indexing
        // matches ADR-0025 § 3 — only `id` and `normalizedTitle` are
        // queried (and `id == normalizedTitle` by construction; both
        // are indexed for safety against future operator queries that
        // happen to name one or the other).
        //
        // No TTL (DefaultTtlSeconds = null). Lookup rows are bounded by
        // the OPDB machine catalog (~2,400 machines) and refreshed on
        // every OPDB sync — there is no stale-row accumulation problem
        // for TTL to solve. Auto-expiring rows would silently break
        // point-reads between syncs and force the cross-partition
        // fallback for the affected titles.
        new()
        {
            Name = "machine_title_lookups",
            PartitionKeyPath = "/normalizedTitle",
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/normalizedTitle/?"],
                ExcludedPaths = ["/*"],
            },
        },
        // Curated landing-page featured machines per ADR-0026 § Landing surface.
        // Seeded by --seed-featured-machines from data/seeds/featured_machines.v1.json.
        // Partition key is /slug (= document id) so every read is a pure point-lookup
        // with no secondary index — mirrors machine_title_lookups. Selective indexing:
        // only display_order is indexed (sort key for the landing strip); title and
        // tagline are display-only, never queried, and excluded to save RU on seed
        // upserts. No TTL (DefaultTtlSeconds = null): the curated set is static between
        // deploys and is replaced wholesale by re-running --seed-featured-machines.
        // Auto-expiring rows would silently break the landing page between seed runs.
        new()
        {
            Name = "featured_machines",
            PartitionKeyPath = "/slug",
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/slug/?", "/display_order/?"],
                ExcludedPaths = ["/*"],
            },
        },
        // Phase 4 W3-2 RAG ingestion containers. The hosted-service
        // consumer (`PinballWizard.RagIngestionWorker`) reads the
        // `scraped_documents` change feed, writes lease checkpoints to
        // `rag_leases`, hash-state rows to `rag_index_state`, and
        // failed-document records to `rag_dead_letters`. Declared in
        // the Phase 1 defaults (rather than gated behind `deployPhase2`)
        // because `--ensure-cosmos-containers` is the canonical creator
        // per ADR-0012 and the Bicep wires the KEDA scaler against
        // `scraped_documents` unconditionally — the change-feed
        // subscription needs the source container to exist on first
        // worker boot. Idempotent: existing containers with matching
        // partition keys are no-ops.
        new() { Name = "scraped_documents", PartitionKeyPath = "/machine_id" },
        // `rag_leases` — Change Feed processor lease container. Partition key
        // /id is required by the Cosmos SDK's ChangeFeedProcessorBuilder.
        // ARM accepts /id as a partition key path (it is not a system-property
        // restriction — see ADR-0012). Declaring it here keeps --ensure-cosmos-
        // containers fully idempotent: a fresh environment gets the container
        // before the worker starts (avoiding the 404 crash on first boot).
        new() { Name = "rag_leases", PartitionKeyPath = "/id" },
        // Selective indexing on `rag_index_state` per ADR-0025 § 3:
        // every read path is either a deterministic point-read by
        // `idx_<document_id>` (CosmosBackedIndexState) or the
        // reconciler's `SELECT TOP @n * FROM c ORDER BY c.recorded_utc
        // DESC` (CosmosAiSearchRagReconciler). Indexing only `id`,
        // `document_id`, and `recorded_utc` keeps the ORDER-BY query
        // efficient while saving RU on every upsert. NOTE: the JSON
        // property is `recorded_utc` (snake_case via JsonPropertyName);
        // Cosmos uses the JSON-on-the-wire path for indexing.
        new()
        {
            Name = "rag_index_state",
            PartitionKeyPath = "/document_id",
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/document_id/?", "/recorded_utc/?"],
                ExcludedPaths = ["/*"],
            },
        },
        // Selective indexing on `rag_dead_letters` per ADR-0025 § 3:
        // SDK access is point-reads by `dl_<document_id>`
        // (CosmosBackedDeadLetterSink). The remaining indexed paths
        // support operator queries in the Cosmos Data Explorer when
        // investigating failed documents — `document_id` for direct
        // lookup, `attempt_count` to triage stuck deliveries, and
        // `last_attempt_utc` to filter by recency. JSON property names
        // (snake_case) are the canonical paths.
        //
        // 90-day TTL (7_776_000 seconds) per ADR-0025 § 3 — failed
        // deliveries that haven't been investigated in 90 days are
        // either stale (the underlying issue self-healed and the
        // failure log is no longer actionable) or in need of operator
        // intervention that hasn't happened. Either way the row's
        // ongoing storage RU isn't earning its keep. Operators who need
        // a longer-retention forensics record can mirror to Log
        // Analytics. The other RAG containers (`rag_leases`,
        // `rag_index_state`) intentionally have no TTL: leases encode
        // ownership state that must persist until released, and the
        // index-state container is the canonical hash store the
        // pipeline consults on every change-feed delivery.
        new()
        {
            Name = "rag_dead_letters",
            PartitionKeyPath = "/document_id",
            DefaultTtlSeconds = 7_776_000,
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/document_id/?", "/attempt_count/?", "/last_attempt_utc/?"],
                ExcludedPaths = ["/*"],
            },
        },
        // scraped_documents_raw: scraper write target (Phase 4.5 document-linking)
        // Partition key: /document_id (one record per unique file URL).
        // Written by scrapers; read + updated by the linker.
        // Selective indexing: link_status and document_type queried in bulk;
        // everything else is point-reads by document_id.
        new()
        {
            Name = "scraped_documents_raw",
            PartitionKeyPath = "/document_id",
            IndexingPolicy = new CosmosIndexingPolicyOptions
            {
                IncludedPaths = ["/document_id/?", "/link_status/?", "/document_type/?"],
                ExcludedPaths = ["/*"],
            },
        },
        // link_overrides: admin feedback store (Phase 4.5 document-linking)
        // Partition key: /source_pattern (= id). One record per
        // {discovery_url}|{document_type} pattern. Upsert semantics.
        // No TTL — overrides are permanent until explicitly revoked.
        new()
        {
            Name = "link_overrides",
            PartitionKeyPath = "/source_pattern",
        },
        // admin_settings: runtime-mutable Wizard configuration (admin
        // settings plan, PR-B1). Partition key /key (= id) — every read is
        // a point lookup by well-known key; CosmosAdminSettingsRepository
        // fronts it with a 2-minute TTL cache so steady-state RU is ~zero.
        // No TTL (DefaultTtlSeconds = null): settings are permanent until
        // explicitly changed or deleted (delete = revert to the IOptions
        // default) — auto-expiry would silently revert operator decisions.
        // Default indexing policy: the container holds tens of tiny docs;
        // GetAllAsync's cross-partition scan at that scale costs less RU
        // than the policy churn of maintaining a selective allowlist.
        new()
        {
            Name = "admin_settings",
            PartitionKeyPath = "/key",
        },
        // admin_prompts: per-agent prompt override store (PR-B3).
        // Partition key /agent_name — every read is scoped to one agent
        // (GetActiveAsync: single point-read; GetVersionsAsync: single-
        // partition scan). Multiple version rows live in the same
        // partition (id = "{agent_name}:v{version}"), so the partition
        // scan for all versions of an agent is a single-partition query
        // with no cross-partition fan-out.
        //
        // No TTL (DefaultTtlSeconds = null): prompt versions are
        // audit records — an admin needs to be able to view and
        // re-activate prior versions; auto-expiry would silently
        // destroy that audit trail.
        //
        // Default indexing policy (null): the container holds at most
        // ~20 rows per agent (4 agents × several versions). The only
        // non-point-read path is GetVersionsAsync's per-partition scan,
        // which touches all rows in one partition — default Cosmos
        // indexing is sufficient and the policy-maintenance overhead
        // of a selective allowlist would cost more than it saves at
        // this cardinality.
        new()
        {
            Name = "admin_prompts",
            PartitionKeyPath = "/agent_name",
        },
    ];

    /// <summary>
    /// Optional override for the application name reported on the
    /// connection. Helpful in Cosmos diagnostics for distinguishing the
    /// scraper job from the API.
    /// </summary>
    public string? ApplicationName { get; init; }

    /// <summary>
    /// Preferred region(s) for client read routing. Defaults to East US
    /// 2 to match the deployment region.
    /// </summary>
    public IReadOnlyList<string> PreferredRegions { get; init; } = ["East US 2"];
}

/// <summary>
/// Per-container declaration. The bootstrapper creates the container
/// if absent on startup; if present, the partition-key path is
/// asserted (mismatch is a fatal startup error since the data layout
/// has drifted from the configuration).
/// </summary>
public sealed class CosmosContainerOptions
{
    /// <summary>Container name (e.g., <c>machines</c>, <c>ingestion_sources</c>).</summary>
    [Required]
    public required string Name { get; init; }

    /// <summary>Partition key path including the leading slash (e.g., <c>/manufacturer</c>, <c>/userId</c>).</summary>
    [Required]
    [RegularExpression("^/[a-zA-Z0-9_]+$", ErrorMessage = "Partition key path must start with '/' and contain only alphanumeric characters and underscores.")]
    public required string PartitionKeyPath { get; init; }

    /// <summary>
    /// Default time-to-live in seconds for documents in this container.
    /// Null disables TTL. -1 enables TTL but with no default expiration
    /// (per-document TTL only).
    /// </summary>
    public int? DefaultTtlSeconds { get; init; }

    /// <summary>
    /// Optional indexing policy override per ADR-0025 § 3. When null
    /// (the default), the container is created with Cosmos's default
    /// policy (all paths indexed). When set, only the listed
    /// <see cref="CosmosIndexingPolicyOptions.IncludedPaths"/> are
    /// indexed and the listed
    /// <see cref="CosmosIndexingPolicyOptions.ExcludedPaths"/> are
    /// excluded. The provisioners apply the policy on container
    /// create; on existing containers, drift is logged at warning (not
    /// thrown) because indexing policy can be re-applied without data
    /// loss — partition-key drift remains fatal because the layout has
    /// already been committed and would silently misroute writes.
    /// </summary>
    public CosmosIndexingPolicyOptions? IndexingPolicy { get; init; }
}

/// <summary>
/// Indexing policy override for a Cosmos container. Maps directly onto
/// the SDK's <c>IndexingPolicy.IncludedPaths</c> /
/// <c>IndexingPolicy.ExcludedPaths</c> shape (and the ARM equivalent).
/// Path syntax uses Cosmos's JSON-pointer-with-wildcard form, e.g.
/// <c>/id/?</c> for a single property or <c>/*</c> for everything.
/// Cosmos requires at least one included path; the
/// provisioners pass the configured paths through unmodified, so an
/// empty <see cref="IncludedPaths"/> would yield a policy Cosmos
/// rejects on create.
/// </summary>
public sealed class CosmosIndexingPolicyOptions
{
    /// <summary>Paths to index (Cosmos path syntax, e.g. <c>/id/?</c>).</summary>
    public IReadOnlyList<string> IncludedPaths { get; init; } = [];

    /// <summary>Paths to exclude from indexing (e.g. <c>/*</c> for everything not listed).</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
}
