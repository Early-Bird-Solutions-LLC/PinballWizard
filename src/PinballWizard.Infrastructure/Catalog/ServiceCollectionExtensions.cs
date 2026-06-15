using System.Globalization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Infrastructure.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Catalog;

public static class ServiceCollectionExtensions
{
    // Canonical manufacturer partition-key strings. These are the normalized
    // keys written by OpdbMachineMapper.NormalizeManufacturerKey — the same
    // values the change-feed handler will encounter as change.Manufacturer.
    // Derived from ManufacturerMatchTokens in OpdbMachineMapper.cs; update
    // both together if a new manufacturer is added to the scraper fleet.
    private static readonly IReadOnlyList<string> KnownManufacturers =
    [
        "stern",
        "jjp",
        "americanpinball",
        "spooky",
        "multimorphic",
        "cgc",
        "haggis",
        "pinballbrothers",
        "dutch",
        "barrelsoffun",
    ];

    // Wires the catalog_stats change-feed projection consumer into DI.
    // Call this in the worker host AFTER AddCosmosChangeFeedRagIngestion,
    // which registers IDeadLetterSink (consumed by the second hosted service
    // registered here). This runs a SECOND change-feed consumer against
    // `scraped_documents` with its own lease container (`catalog_stats_leases`)
    // and processor name (`catalog-stats`), independent of the RAG consumer.
    //
    // Multi-consumer IOptions resolution: the hosted service ctor requires
    // IOptions<CosmosChangeFeedHostedServiceOptions>. The RAG consumer has
    // already bound "Rag:ChangeFeed" to that type. Rather than registering a
    // second named-options slot (which the BackgroundService ctor doesn't
    // support) we build a fresh CosmosChangeFeedHostedServiceOptions POCO
    // from the resolved CatalogStatsProjectionOptions and wrap it with
    // Options.Create — giving the second consumer fully independent settings
    // without touching the RAG registration.
    //
    // For strict multi-replica correctness, pin the catalog-stats consumer to
    // a single replica via CatalogStatsProjectionOptions.InstanceName or by
    // setting the Container App replica count to 1 for this consumer.
    public static IServiceCollection AddCatalogStatsProjection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CatalogStatsProjectionOptions>()
            .Bind(configuration.GetSection(CatalogStatsProjectionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Register the second IHostedService — the catalog-stats change-feed consumer.
        // Note: IDeadLetterSink must already be registered (AddCosmosChangeFeedRagIngestion
        // registers CosmosBackedDeadLetterSink). If only AddCatalogStatsProjection is called
        // without the RAG registration, the host will fail to resolve IDeadLetterSink at
        // startup — which is the intended behavior (explicit, visible failure per Invariant #17).
        services.AddSingleton<IHostedService>(sp =>
        {
            var catalogOpts = sp.GetRequiredService<IOptions<CatalogStatsProjectionOptions>>().Value;

            // Build per-consumer change-feed options from CatalogStatsProjectionOptions.
            // This avoids reusing the RAG consumer's "Rag:ChangeFeed" binding, which has a
            // different lease container and processor name.
            var cfOpts = new CosmosChangeFeedHostedServiceOptions
            {
                SourceContainerName = catalogOpts.SourceContainerName,
                LeaseContainerName  = catalogOpts.LeaseContainerName,
                ProcessorName       = catalogOpts.ProcessorName,
                InstanceName        = catalogOpts.InstanceName,
                StartFromBeginning  = catalogOpts.StartFromBeginning,
            };
            var cfOptsWrapper = Options.Create(cfOpts);

            var sourceContainer = ResolveContainer(sp, catalogOpts.SourceContainerName);
            var leaseContainer  = ResolveContainer(sp, catalogOpts.LeaseContainerName);

            // Construct the handler inline (internal sealed class — not registered
            // as a named service, just instantiated for this consumer).
            var scrapedDocsRepo = new CosmosRepository<ScrapedDocumentRecord>(
                ResolveContainer(sp, catalogOpts.SourceContainerName),
                sp.GetRequiredService<ILogger<CosmosRepository<ScrapedDocumentRecord>>>());

            var handler = new CatalogStatsChangeFeedHandler(
                scrapedDocsRepo,
                ResolveContainer(sp, "catalog_stats"),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CatalogStatsChangeFeedHandler>>());

            return new CosmosChangeFeedHostedService<RagSourceDocument>(
                sourceContainer,
                leaseContainer,
                handler,
                sp.GetRequiredService<IDeadLetterSink>(),
                static d => d.DocumentId,
                static d => d.Lsn?.ToString(CultureInfo.InvariantCulture),
                sp.GetRequiredService<IOptions<RagIngestionOptions>>(),
                cfOptsWrapper,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CosmosChangeFeedHostedService<RagSourceDocument>>>(),
                reconciler: null); // catalog-stats does not reconcile against AI Search
        });

        return services;
    }

    // Wires the catalog_stats and scraped_documents read repositories for the
    // Web / CLI read side. No hosted service — read-only projection consumers.
    public static IServiceCollection AddCatalogStatsRead(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICatalogStatsReadRepository>(sp =>
            new CosmosCatalogStatsRepository(
                ResolveContainer(sp, "catalog_stats"),
                KnownManufacturers,
                sp.GetRequiredService<ILogger<CosmosRepository<CatalogStatsCosmosRecord>>>()));

        services.AddSingleton<IMachineDocumentReadRepository>(sp =>
            new CosmosMachineDocumentReadRepository(
                ResolveContainer(sp, "scraped_documents"),
                sp.GetRequiredService<IRawDocumentRepository>(),
                sp.GetRequiredService<ILogger<CosmosRepository<ScrapedDocumentRecord>>>()));

        return services;
    }

    private static Container ResolveContainer(IServiceProvider sp, string containerName)
    {
        var client  = sp.GetRequiredService<CosmosClient>();
        var options = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
        return client.GetContainer(options.DatabaseName, containerName);
    }
}
