using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Documents;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

public static class ServiceCollectionExtensions
{
    // Wires the W3-2 Infrastructure-layer change-feed consumer for
    // `scraped_documents` into DI. Caller (the W3-2 worker host)
    // must already have:
    //   - `AddCosmosPersistence(configuration)` for the Cosmos client +
    //     CosmosOptions (so the source + lease containers are reachable
    //     and `--ensure-cosmos-containers` has them in scope).
    //   - `AddRagIngestionPipeline()` from Application for the
    //     orchestrator pipeline.
    //   - `AddAzureFoundryIntegration()` + `AddAzureAiSearchIntegration()`
    //     for the embedder + indexer.
    //   - The chunker + extractor wired (chunking + extraction
    //     ServiceCollectionExtensions).
    //
    // What this method adds:
    //   - Binds `RagIngestionOptions` from `Rag:Ingestion`.
    //   - Binds `CosmosChangeFeedHostedServiceOptions` from `Rag:ChangeFeed`.
    //   - Registers `IIndexState` → `CosmosBackedIndexState` (Cosmos-backed
    //     against the `rag_index_state` container — overrides the
    //     Application-side TryAddSingleton placeholder).
    //   - Registers `IDeadLetterSink` → `CosmosBackedDeadLetterSink`.
    //   - Registers `IDocumentBytesSource` → `HttpDocumentBytesSource`
    //     via a typed HttpClient.
    //   - Registers the concrete `ICosmosChangeFeedHandler<RagSourceDocument>`
    //     bridge.
    //   - Registers the generic `CosmosChangeFeedHostedService<RagSourceDocument>`
    //     as the BackgroundService that drives the loop.
    public static IServiceCollection AddCosmosChangeFeedRagIngestion(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RagIngestionOptions>()
            .Bind(configuration.GetSection(RagIngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<CosmosChangeFeedHostedServiceOptions>()
            .Bind(configuration.GetSection(CosmosChangeFeedHostedServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Cosmos-backed sinks. We deliberately replace any placeholder
        // IIndexState the Application-side wires up with the Cosmos
        // impl: the worker is the canonical home for `IIndexState`
        // persistence and the worker is the only host that consumes
        // this method.
        services.RemoveAll<IIndexState>();
        services.AddSingleton<IIndexState>(sp => new CosmosBackedIndexState(
            ResolveContainer(sp, "rag_index_state"),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosBackedIndexState>>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IDeadLetterSink>(sp => new CosmosBackedDeadLetterSink(
            ResolveContainer(sp, "rag_dead_letters"),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosBackedDeadLetterSink>>()));

        // Typed HttpClient gives the bytes source automatic resilience
        // (via the ServiceDefaults standard handler) + per-message
        // logging via the host's HttpClient factory.
        AddBlobDocumentBytesSource(services, configuration);

        services.AddSingleton<ICosmosChangeFeedHandler<RagSourceDocument>, ScrapedDocumentChangeFeedHandler>();

        // Reconciler — Cosmos `rag_index_state` reader + AI Search
        // verifier. Registered unconditionally; the hosted service
        // only invokes it when `RagIngestionOptions.ReconcileOnStartup
        // = true`. SearchClient is constructed inline (matches the
        // per-factory construction pattern used by
        // `AddAzureAiSearchIntegration.BuildRagIndexer` /
        // `BuildRagRetriever` — a top-level SearchClient registration
        // would be a cleaner refactor target but is out of scope for
        // the reconcile follow-up).
        services.AddSingleton<IRagReconciler>(sp =>
        {
            var aiSearchOptions = sp.GetRequiredService<IOptions<AiSearchOptions>>().Value;
            var searchClient = new SearchClient(
                new Uri(aiSearchOptions.Endpoint),
                aiSearchOptions.IndexName,
                new DefaultAzureCredential());

            return new CosmosAiSearchRagReconciler(
                ResolveContainer(sp, "rag_index_state"),
                searchClient,
                sp.GetRequiredService<IOptions<RagIngestionOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosAiSearchRagReconciler>>());
        });

        services.AddSingleton<IHostedService>(sp =>
        {
            var changeFeedOpts = sp.GetRequiredService<IOptions<CosmosChangeFeedHostedServiceOptions>>().Value;
            var sourceContainer = ResolveContainer(sp, changeFeedOpts.SourceContainerName);
            var leaseContainer = ResolveContainer(sp, changeFeedOpts.LeaseContainerName);

            return new CosmosChangeFeedHostedService<RagSourceDocument>(
                sourceContainer,
                leaseContainer,
                sp.GetRequiredService<ICosmosChangeFeedHandler<RagSourceDocument>>(),
                sp.GetRequiredService<IDeadLetterSink>(),
                static d => d.DocumentId,
                static d => d.Lsn?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                sp.GetRequiredService<IOptions<RagIngestionOptions>>(),
                sp.GetRequiredService<IOptions<CosmosChangeFeedHostedServiceOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosChangeFeedHostedService<RagSourceDocument>>>(),
                sp.GetService<IRagReconciler>());
        });

        return services;
    }

    // Registers `IRagBackfillService` for the `--run-rag-backfill` CLI
    // command. Requires Cosmos + AI Search + AI Foundry integration to
    // already be wired by the caller. Only registers the handler and the
    // backfill service — does NOT register the BackgroundService, the
    // lease-lag estimator, the dead-letter sink, or the reconciler
    // (those belong to the worker host, not the CLI).
    //
    // The caller must also bind `Rag:Ingestion` options before calling
    // this method (typically via AddOptions<RagIngestionOptions>().Bind(...)).
    public static IServiceCollection AddRagBackfillService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RagIngestionOptions>()
            .Bind(configuration.GetSection(RagIngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<CosmosChangeFeedHostedServiceOptions>()
            .Bind(configuration.GetSection(CosmosChangeFeedHostedServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Cosmos-backed IIndexState — same as the worker host wires.
        // RemoveAll first so the Application-layer TryAddSingleton
        // placeholder doesn't win.
        services.RemoveAll<IIndexState>();
        services.AddSingleton<IIndexState>(sp => new CosmosBackedIndexState(
            ResolveContainer(sp, "rag_index_state"),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosBackedIndexState>>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<ICosmosChangeFeedHandler<RagSourceDocument>, ScrapedDocumentChangeFeedHandler>();

        // HttpClient for IDocumentBytesSource — same as the worker host.
        AddBlobDocumentBytesSource(services, configuration);

        services.AddSingleton<IRagBackfillService>(sp =>
        {
            var changeFeedOpts = sp.GetRequiredService<IOptions<CosmosChangeFeedHostedServiceOptions>>().Value;
            var sourceContainer = ResolveContainer(sp, changeFeedOpts.SourceContainerName);

            return new CosmosRagBackfillService(
                sourceContainer,
                sp.GetRequiredService<ICosmosChangeFeedHandler<RagSourceDocument>>(),
                sp.GetRequiredService<IOptions<RagIngestionOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosRagBackfillService>>());
        });

        return services;
    }

    private static Container ResolveContainer(IServiceProvider sp, string containerName)
    {
        var client = sp.GetRequiredService<CosmosClient>();
        var options = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
        return client.GetContainer(options.DatabaseName, containerName);
    }

    // Registers IDocumentBytesSource as a BlobDocumentBytesSource decorator over the
    // HTTP source: serves bytes from the pinwiz-raw blob container when the caller
    // supplies a blob name (Task 3/4 forward path), falling back to HTTP when the
    // blob is absent or when the caller passes a raw document URL (the current RAG
    // change-feed path — source_type is not present in scraped_documents so the full
    // blob key cannot be derived from the URL alone; see BlobDocumentBytesSource for
    // full rationale). The inner HTTP source keeps its typed-client.
    //
    // Also wires AddDocumentBlobStore so the blob store is available for injection
    // when the Storage:BlobEndpoint or ConnectionStrings:blobs config is present.
    // When neither is configured (e.g. a stripped-down test host), AddDocumentBlobStore
    // is a no-op and IDocumentBlobStore will be absent from DI — BlobDocumentBytesSource
    // requires IDocumentBlobStore so callers must ensure storage is configured.
    private static void AddBlobDocumentBytesSource(IServiceCollection services, IConfiguration configuration)
    {
        // Wire the blob store (pinwiz-raw container, managed identity in deployed env,
        // Azurite connection string in local dev via Aspire).
        services.AddDocumentBlobStore(configuration);

        // Inner HTTP source keeps its typed-client (resilience + handler rotation
        // via IHttpClientFactory). Registered transient — the decorator must NOT
        // capture a single instance for process life (that would pin one HttpClient
        // handler and defeat factory rotation/DNS-refresh). The decorator resolves
        // a fresh inner per fallback via the captured IServiceProvider.
        services.AddHttpClient<HttpDocumentBytesSource>();
        services.AddSingleton<IDocumentBytesSource>(sp =>
        {
            var store = sp.GetRequiredService<IDocumentBlobStore>();
            var logger = sp.GetRequiredService<ILogger<BlobDocumentBytesSource>>();
            // Fresh HTTP inner per fallback — preserves typed-client handler rotation.
            return new BlobDocumentBytesSource(
                store,
                () => sp.GetRequiredService<HttpDocumentBytesSource>(),
                logger);
        });
    }
}
