using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Scraper.Tests.Persistence.Cosmos;

/// <summary>
/// Pins the selective indexing policy decisions from ADR-0025 § 3 onto
/// the <see cref="CosmosOptions"/> defaults so that drift between the
/// ADR and the runtime configuration trips a unit-test failure rather
/// than surviving as a silent RU-cost regression. Every PR that touches
/// the per-container <see cref="CosmosContainerOptions.IndexingPolicy"/>
/// must update both the ADR and these tests.
/// </summary>
public sealed class IndexingPolicyContractTests
{
    [Fact]
    public void Defaults_RagIndexState_HasSelectiveIndexing()
    {
        // The reconciler issues `SELECT TOP @n * FROM c ORDER BY
        // c.recorded_utc DESC`, so `recorded_utc` must be indexed.
        // The hash-tracking point read uses `id` and the partition key
        // (document_id), but no other property is queried — the rest
        // are excluded to halve the per-write RU cost.
        var indexState = AssertContainer("rag_index_state");
        var policy = Assert.IsType<CosmosIndexingPolicyOptions>(indexState.IndexingPolicy);
        Assert.Equal(["/id/?", "/document_id/?", "/recorded_utc/?"], policy.IncludedPaths);
        Assert.Equal(["/*"], policy.ExcludedPaths);
    }

    [Fact]
    public void Defaults_RagDeadLetters_HasSelectiveIndexing()
    {
        // SDK access is point-reads only (`dl_<document_id>`); the
        // remaining indexed paths (`document_id`, `attempt_count`,
        // `last_attempt_utc`) support operator queries in the Cosmos
        // Data Explorer when triaging failed deliveries. Using
        // snake_case to match the JSON property names emitted by the
        // `[JsonPropertyName]` attributes on `DeadLetterDocument`.
        var deadLetters = AssertContainer("rag_dead_letters");
        var policy = Assert.IsType<CosmosIndexingPolicyOptions>(deadLetters.IndexingPolicy);
        Assert.Equal(
            ["/id/?", "/document_id/?", "/attempt_count/?", "/last_attempt_utc/?"],
            policy.IncludedPaths);
        Assert.Equal(["/*"], policy.ExcludedPaths);
    }

    [Fact]
    public void Defaults_RagLeases_HasNoIndexingPolicyOverride()
    {
        // The lease container is owned by the SDK's
        // `Cosmos.ChangeFeedProcessor` — its document shape and query
        // patterns are SDK-internal. A selective policy that drifts
        // from a future SDK version's query surface would manifest as
        // a silent perf regression on change-feed processing. ADR-0025
        // § 3 explicitly leaves this container at the default policy.
        var leases = AssertContainer("rag_leases");
        Assert.Null(leases.IndexingPolicy);
    }

    [Fact]
    public void Defaults_Machines_HasNoIndexingPolicyOverride()
    {
        // The `machines` read-side query patterns are still being
        // tuned (PR 5 in the Cosmos for User Delight track adds
        // `machine_title_lookups`; future tools may add structured-
        // record queries), so we keep default (all-paths) indexing
        // until the access pattern stabilizes.
        var machines = AssertContainer("machines");
        Assert.Null(machines.IndexingPolicy);
    }

    [Fact]
    public void Defaults_IngestionSources_HasNoIndexingPolicyOverride()
    {
        // The Admin UI may surface arbitrary fields from
        // `ingestion_sources`; selective indexing here would prematurely
        // constrain the UI's filter surface. Default indexing.
        var ingestionSources = AssertContainer("ingestion_sources");
        Assert.Null(ingestionSources.IndexingPolicy);
    }

    [Fact]
    public void Defaults_ScrapedDocuments_HasNoIndexingPolicyOverride()
    {
        // The Change Feed reader uses Cosmos system fields (`_lsn`,
        // `_ts`) which are auto-indexed regardless of the policy, but
        // a future per-machine reindex tool may issue a single-
        // partition query against the source container. We keep
        // default indexing until the access pattern stabilizes.
        var scraped = AssertContainer("scraped_documents");
        Assert.Null(scraped.IndexingPolicy);
    }

    private static CosmosContainerOptions AssertContainer(string name)
    {
        var options = new CosmosOptions();
        return Assert.Single(options.Containers, c => c.Name == name);
    }
}
