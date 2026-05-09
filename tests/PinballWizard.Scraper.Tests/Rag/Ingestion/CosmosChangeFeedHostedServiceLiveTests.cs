using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Ingestion;

// Live-contract tests for the W3-2 hosted-service stack against a
// deployed Cosmos account. Mirrors the gating + env-var layout of
// `AiSearchRagIndexerLiveTests` / `LiveSearchCorpusToolTests` so an
// operator who runs one suite against deployed infra can run the
// others with the same prerequisites.
//
// Gated by PINBALL_WIZARD_LIVE_RAG_TESTS=1; CI does not set it.
// Required env vars when enabled:
//   AZURE_COSMOS_ACCOUNT_ENDPOINT — https://pinwiz-cosmos-dev-XXXX.documents.azure.com:443/
//   AZURE_COSMOS_DATABASE_NAME    — pinwiz (or env-overridden)
// The signed-in identity must hold:
//   - Cosmos DB Built-in Data Contributor (`00000000-0000-0000-0000-000000000002`)
//     on the database account, scoped to the database.
//
// Scope: this file proves the data-plane shape — that
// `CosmosBackedDeadLetterSink` and `CosmosBackedIndexState` round-trip
// records against the real `rag_dead_letters` and `rag_index_state`
// containers without 401 / 404 / serialization surprises. The
// `CosmosChangeFeedHostedService<T>` end-to-end loop (subscribe →
// receive → handle → checkpoint) is covered by the operator hand-off
// chain (build worker image → swap ACA → tail logs); replicating it
// here would require provisioning the worker as a child process and
// is out of scope for unit-suite live tests.
public sealed class CosmosChangeFeedHostedServiceLiveTests
{
    private const string EnableEnvVar = "PINBALL_WIZARD_LIVE_RAG_TESTS";

    private static bool IsLiveContractEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnableEnvVar);
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeadLetterSink_LiveContract_UpsertThenGetRoundTripsCleanly()
    {
        if (!IsLiveContractEnabled())
        {
            return;
        }

        var (cosmosClient, databaseName) = BuildClient();

        try
        {
            var container = cosmosClient.GetContainer(databaseName, "rag_dead_letters");
            var sink = new CosmosBackedDeadLetterSink(
                container,
                NullLogger<CosmosBackedDeadLetterSink>.Instance);

            var documentId = $"livetest_dl_{Guid.NewGuid():N}";
            var nowUtc = DateTimeOffset.UtcNow;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

                // Get on a missing row returns null.
                Assert.Null(await sink.GetAsync(documentId, cts.Token));

                // Upsert a record + read it back.
                var record = new DeadLetterRecord(
                    DocumentId: documentId,
                    AttemptCount: 2,
                    LastAttemptUtc: nowUtc,
                    ErrorClass: "InvalidOperationException",
                    ErrorMessage: "live-test simulated failure",
                    ChangeLsn: "1234");

                await sink.UpsertAsync(record, cts.Token);

                var roundTrip = await sink.GetAsync(documentId, cts.Token);
                Assert.NotNull(roundTrip);
                Assert.Equal(documentId, roundTrip!.DocumentId);
                Assert.Equal(2, roundTrip.AttemptCount);
                Assert.Equal("InvalidOperationException", roundTrip.ErrorClass);
                Assert.Equal("1234", roundTrip.ChangeLsn);
            }
            finally
            {
                // Cleanup: delete the test row regardless of outcome.
                try
                {
                    await container.DeleteItemAsync<DeadLetterDocument>(
                        DeadLetterDocument.RowIdPrefix + documentId,
                        new PartitionKey(documentId));
                }
                catch (Exception)
                {
                    // Swallow cleanup failures intentionally — the test
                    // result is what matters. Scoped to `catch (Exception)`
                    // (not bare) so OOM / cancellation propagate per the
                    // project's no-bare-catch policy.
                }
            }
        }
        finally
        {
            cosmosClient.Dispose();
        }
    }

    [Fact]
    public async Task IndexState_LiveContract_RecordThenReadRoundTripsCleanly()
    {
        if (!IsLiveContractEnabled())
        {
            return;
        }

        var (cosmosClient, databaseName) = BuildClient();

        try
        {
            var container = cosmosClient.GetContainer(databaseName, "rag_index_state");
            var indexState = new CosmosBackedIndexState(
                container,
                NullLogger<CosmosBackedIndexState>.Instance,
                TimeProvider.System);

            var documentId = $"livetest_ix_{Guid.NewGuid():N}";

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

                Assert.Null(await indexState.GetLastIndexedHashAsync(documentId, cts.Token));

                await indexState.RecordIndexedAsync(
                    documentId, "hash-livetest", chunkCount: 7, failureCount: 0, cts.Token);

                Assert.Equal(
                    "hash-livetest",
                    await indexState.GetLastIndexedHashAsync(documentId, cts.Token));
            }
            finally
            {
                try
                {
                    await container.DeleteItemAsync<IndexStateDocument>(
                        IndexStateDocument.RowIdPrefix + documentId,
                        new PartitionKey(documentId));
                }
                catch (Exception)
                {
                    // Swallow cleanup failures intentionally — the test
                    // result is what matters. Scoped to `catch (Exception)`
                    // (not bare) so OOM / cancellation propagate per the
                    // project's no-bare-catch policy.
                }
            }
        }
        finally
        {
            cosmosClient.Dispose();
        }
    }

    private static (CosmosClient Client, string DatabaseName) BuildClient()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_COSMOS_ACCOUNT_ENDPOINT")
            ?? throw new InvalidOperationException(
                "AZURE_COSMOS_ACCOUNT_ENDPOINT is required when PINBALL_WIZARD_LIVE_RAG_TESTS=1.");
        var databaseName = Environment.GetEnvironmentVariable("AZURE_COSMOS_DATABASE_NAME") ?? "pinwiz";

        var client = new CosmosClient(
            endpoint,
            new DefaultAzureCredential(),
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Direct,
                ConsistencyLevel = ConsistencyLevel.Session,
            });
        return (client, databaseName);
    }
}
