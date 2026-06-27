using System.Globalization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Infrastructure.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Catalog;

public static class ServiceCollectionExtensions
{

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
            // Reads the narrow doc-type projection (tolerates pre-#318 documents),
            // not the full write-model ScrapedDocumentRecord.
            var scrapedDocsRepo = new CosmosRepository<ScrapedDocumentTypeProjection>(
                ResolveContainer(sp, catalogOpts.SourceContainerName),
                sp.GetRequiredService<ILogger<CosmosRepository<ScrapedDocumentTypeProjection>>>());

            var handler = new CatalogStatsChangeFeedHandler(
                scrapedDocsRepo,
                ResolveContainer(sp, "catalog_stats"),
                sp.GetRequiredService<IMachineRepository>(),
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
                sp.GetRequiredService<ILogger<CosmosRepository<CatalogStatsCosmosRecord>>>()));

        services.AddSingleton<IMachineDocumentReadRepository>(sp =>
            new CosmosMachineDocumentReadRepository(
                ResolveContainer(sp, "scraped_documents"),
                sp.GetRequiredService<IRawDocumentRepository>(),
                sp.GetRequiredService<ILogger<CosmosRepository<ScrapedDocumentReadProjection>>>()));

        return services;
    }

    // Registers the catalog_stats rebuild service + its two CosmosRepository<T>
    // dependencies for the --rebuild-catalog-stats CLI verb. Call this inside
    // the cosmosWired gate in Program.cs alongside the other Cosmos-dependent
    // service registrations.
    //
    // CosmosRepository<ScrapedDocumentTypeProjection> reads from `scraped_documents`
    // (single-partition per-machine scan — Tier 1 ADR-0036; narrow projection so the
    // scan tolerates documents predating later `required` fields like edition_scope).
    // CosmosRepository<CatalogStatsCosmosRecord> writes to `catalog_stats`
    // (one upsert per manufacturer — point operation, not a query).
    public static IServiceCollection AddCatalogStatsRebuild(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.AddTransient<ICatalogStatsRebuildService>(sp =>
            new CatalogStatsRebuildService(
                sp.GetRequiredService<IMachineRepository>(),
                new CosmosRepository<ScrapedDocumentTypeProjection>(
                    ResolveContainer(sp, "scraped_documents"),
                    sp.GetRequiredService<ILogger<CosmosRepository<ScrapedDocumentTypeProjection>>>()),
                new CosmosRepository<CatalogStatsCosmosRecord>(
                    ResolveContainer(sp, "catalog_stats"),
                    sp.GetRequiredService<ILogger<CosmosRepository<CatalogStatsCosmosRecord>>>()),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CatalogStatsRebuildService>>()));

        return services;
    }

    private static Container ResolveContainer(IServiceProvider sp, string containerName)
    {
        var client  = sp.GetRequiredService<CosmosClient>();
        var options = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
        return client.GetContainer(options.DatabaseName, containerName);
    }
}
