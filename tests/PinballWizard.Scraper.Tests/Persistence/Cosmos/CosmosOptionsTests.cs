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
    public void Defaults_Containers_IncludesRagLeasesContainer()
    {
        // W3-2 lease container — owned by Cosmos.ChangeFeedProcessor and by
        // the KEDA Cosmos scaler in the Bicep ACA resource. Partition key
        // `/id` matches the SDK's lease-document layout.
        var options = new CosmosOptions();

        var leases = Assert.Single(options.Containers, c => c.Name == "rag_leases");
        Assert.Equal("/id", leases.PartitionKeyPath);
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
    public void Defaults_Containers_HasExactlyTheExpectedContainers()
    {
        // Pin the count so a future addition that drifts from the repository
        // registrations or worker wiring (which would silently leave the new
        // container missing partition-key validation) trips this test as a
        // flag. Phase 1: machines + ingestion_sources. Phase 4 W3-2: adds
        // scraped_documents + rag_leases + rag_index_state + rag_dead_letters.
        var options = new CosmosOptions();
        Assert.Equal(6, options.Containers.Count);
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
}
