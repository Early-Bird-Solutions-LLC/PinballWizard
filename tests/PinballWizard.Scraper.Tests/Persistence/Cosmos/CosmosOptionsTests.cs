using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Scraper.Tests.Persistence.Cosmos;

/// <summary>
/// Tests for <see cref="CosmosOptions"/> defaults. The default container
/// list is load-bearing: <see cref="CosmosBootstrapper"/> uses it on
/// post-deploy smoke-tests (<c>--ensure-cosmos-containers</c>) to create
/// the containers the repositories already write to (the names are
/// hardcoded in the repository registrations). A drift between
/// CosmosOptions defaults and the repository names would silently leave
/// repositories writing to non-existent containers; these tests pin
/// every name and partition-key path against ADR 0011.
/// </summary>
public sealed class CosmosOptionsTests
{
    [Fact]
    public void Defaults_DatabaseName_IsPinwiz()
    {
        var options = new CosmosOptions();
        Assert.Equal("pinwiz", options.DatabaseName);
    }

    [Fact]
    public void Defaults_Containers_IncludesMachinesWithCorrectPartitionKey()
    {
        var options = new CosmosOptions();

        var machines = Assert.Single(options.Containers, c => c.Name == "machines");
        Assert.Equal("/manufacturer", machines.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_Containers_IncludesIngestionSourcesWithCorrectPartitionKey()
    {
        var options = new CosmosOptions();

        var ingestion = Assert.Single(options.Containers, c => c.Name == "ingestion_sources");
        Assert.Equal("/partitionKey", ingestion.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_Containers_IncludesScrapedDocumentsWithMachineIdPartitionKey()
    {
        // W3-2 source container — the change-feed processor in
        // PinballWizard.RagIngestionWorker subscribes to its change feed.
        // Partition key `/machine_id` keeps a machine's documents
        // co-located so a future per-machine reindex can issue a
        // single-partition query rather than a cross-partition scan.
        var options = new CosmosOptions();

        var scraped = Assert.Single(options.Containers, c => c.Name == "scraped_documents");
        Assert.Equal("/machine_id", scraped.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_Containers_DoesNotIncludeRagLeases()
    {
        // rag_leases is SDK-managed: ChangeFeedProcessorBuilder.WithLeaseContainer()
        // auto-creates it with partition key /id on first processor start.
        // ARM rejects /id as a partition key (system property override), so it
        // is excluded from --ensure-cosmos-containers provisioning.
        var options = new CosmosOptions();

        Assert.DoesNotContain(options.Containers, c => c.Name == "rag_leases");
    }

    [Fact]
    public void Defaults_Containers_IncludesRagIndexStateContainer()
    {
        // W3-2 hash-tracking container backing IIndexState. Partition key
        // `/document_id` makes the per-document point-read the natural
        // single-partition lookup.
        var options = new CosmosOptions();

        var indexState = Assert.Single(options.Containers, c => c.Name == "rag_index_state");
        Assert.Equal("/document_id", indexState.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_Containers_IncludesRagDeadLettersContainer()
    {
        // W3-2 per-document failure ledger backing IDeadLetterSink.
        // Partition key `/document_id` keeps the cardinality bounded
        // by document count, not by failure count (re-deliveries upsert
        // the same row).
        var options = new CosmosOptions();

        var deadLetters = Assert.Single(options.Containers, c => c.Name == "rag_dead_letters");
        Assert.Equal("/document_id", deadLetters.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_Containers_IncludesMachineTitleLookupsContainer()
    {
        // Cosmos for User Delight track PR 5 (ADR-0025 § 4) — the
        // Title→OPDB-ID materialized view backing
        // `MachineGroundingTool`'s point-read path. Doc id equals
        // partition-key value (the normalized title) so reads are pure
        // point lookups; the Wizard's `getMachineByTitle` cache-miss
        // path drops from ~50-150ms cross-partition `STRINGEQUALS`
        // to ~5+5ms two-point-read.
        var options = new CosmosOptions();

        var lookup = Assert.Single(options.Containers, c => c.Name == "machine_title_lookups");
        Assert.Equal("/normalizedTitle", lookup.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_Containers_HasExactlyTheExpectedContainers()
    {
        // Pin the count so a future addition that drifts from the repository
        // registrations or worker wiring (which would silently leave the new
        // container missing partition-key validation) trips this test as a
        // flag. Phase 1: machines + ingestion_sources. Phase 4 W3-2: adds
        // scraped_documents + rag_index_state + rag_dead_letters (rag_leases
        // is SDK-managed, excluded from ARM provisioning — see
        // Defaults_Containers_DoesNotIncludeRagLeases). Cosmos for User
        // Delight PR 5: adds machine_title_lookups. Phase 5 Wave 2 PR-L2:
        // adds featured_machines (landing-page strip).
        var options = new CosmosOptions();
        Assert.Equal(7, options.Containers.Count);
    }

    [Fact]
    public void Defaults_AccountEndpoint_IsNull()
    {
        // Optional by design — Aspire's AddAzureCosmosClient supplies the
        // CosmosClient via TryAddSingleton, in which case AccountEndpoint
        // is unused. Leaving it null in the default lets standalone CLI
        // runs (no Aspire, no Cosmos config) skip the registration without
        // failing data-annotation validation.
        var options = new CosmosOptions();
        Assert.Null(options.AccountEndpoint);
    }

    [Fact]
    public void Defaults_RagDeadLetters_HasNinetyDayTtl()
    {
        // Per ADR-0025 § 3 — failed-delivery rows that have not been
        // investigated in 90 days are either stale or in need of
        // operator intervention that hasn't happened. Either way the
        // ongoing storage RU isn't earning its keep. 7_776_000 seconds
        // = 90 days exactly (60 * 60 * 24 * 90).
        var options = new CosmosOptions();

        var deadLetters = Assert.Single(options.Containers, c => c.Name == "rag_dead_letters");
        Assert.Equal(7_776_000, deadLetters.DefaultTtlSeconds);
    }

    [Fact]
    public void Defaults_RagLeases_IsNotArmManaged()
    {
        // rag_leases is excluded from ARM provisioning (see
        // Defaults_Containers_DoesNotIncludeRagLeases). No TTL test needed
        // here — the SDK creates the lease container without a TTL by default,
        // and TTL on leases would strand in-flight continuation tokens.
        var options = new CosmosOptions();

        Assert.DoesNotContain(options.Containers, c => c.Name == "rag_leases");
    }

    [Fact]
    public void Defaults_RagIndexState_HasNoTtl()
    {
        // The index-state container is the canonical hash store the
        // pipeline consults on every change-feed delivery. A TTL here
        // would silently force a re-index of every document whose
        // hash row had expired even when the underlying document
        // hadn't actually changed — defeating the dedup contract.
        var options = new CosmosOptions();

        var indexState = Assert.Single(options.Containers, c => c.Name == "rag_index_state");
        Assert.Null(indexState.DefaultTtlSeconds);
    }

    [Fact]
    public void Defaults_MachineTitleLookups_HasNoTtl()
    {
        // The lookup container is bounded by the OPDB catalog (~2,400
        // machines) and refreshed on every OPDB sync — no stale-row
        // accumulation problem for TTL to solve. Auto-expiring rows
        // would silently break point-reads between syncs and force
        // the cross-partition fallback for the affected titles.
        var options = new CosmosOptions();

        var lookup = Assert.Single(options.Containers, c => c.Name == "machine_title_lookups");
        Assert.Null(lookup.DefaultTtlSeconds);
    }

    [Fact]
    public void Defaults_Containers_IncludesFeaturedMachinesContainer()
    {
        // Phase 5 Wave 2 PR-L2 — curated landing-page strip per ADR-0026.
        // Partition key /slug (= document id) so every read is a pure
        // point-lookup with no secondary index, mirroring machine_title_lookups.
        var options = new CosmosOptions();

        var featured = Assert.Single(options.Containers, c => c.Name == "featured_machines");
        Assert.Equal("/slug", featured.PartitionKeyPath);
    }

    [Fact]
    public void Defaults_FeaturedMachines_HasNoTtl()
    {
        // The curated list is static between deploys and replaced wholesale
        // by re-running --seed-featured-machines. Auto-expiring rows would
        // silently break the landing page between seed runs.
        var options = new CosmosOptions();

        var featured = Assert.Single(options.Containers, c => c.Name == "featured_machines");
        Assert.Null(featured.DefaultTtlSeconds);
    }

    [Fact]
    public void Defaults_FeaturedMachines_HasSelectiveIndexingPolicy()
    {
        // Per ADR-0025 § 6 — only display_order is indexed (sort key for the
        // landing strip). Title and tagline are display-only and excluded to
        // save RU on seed upserts.
        var options = new CosmosOptions();

        var featured = Assert.Single(options.Containers, c => c.Name == "featured_machines");
        Assert.NotNull(featured.IndexingPolicy);
        Assert.Contains("/display_order/?", featured.IndexingPolicy!.IncludedPaths);
        Assert.Contains("/*", featured.IndexingPolicy.ExcludedPaths);
    }

    [Fact]
    public void Defaults_Phase1Containers_HaveNoTtl()
    {
        // `machines` and `ingestion_sources` are durable catalogs —
        // any TTL would silently delete catalog rows and break
        // downstream tools that rely on point-reads. `scraped_documents`
        // is the source of truth for raw scraped content; retention
        // policy is operator-managed (purge via the catalog reconciler),
        // not TTL-driven.
        var options = new CosmosOptions();

        Assert.Null(Assert.Single(options.Containers, c => c.Name == "machines").DefaultTtlSeconds);
        Assert.Null(Assert.Single(options.Containers, c => c.Name == "ingestion_sources").DefaultTtlSeconds);
        Assert.Null(Assert.Single(options.Containers, c => c.Name == "scraped_documents").DefaultTtlSeconds);
    }
}
