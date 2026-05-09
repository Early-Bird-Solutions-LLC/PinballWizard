using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Rag.Ingestion;

public static class ServiceCollectionExtensions
{
    // Wires the W3-2 Application-layer ingestion pipeline (build-spec
    // § Phase 4 item 18) into DI. Pipeline implementation is pure
    // orchestration — Infrastructure-layer dependencies (`IIndexState`
    // backed by Cosmos, the Cosmos Change Feed hosted service) register
    // separately in Infrastructure's service-collection extensions.
    //
    // Mirrors the AddHybridChunker precedent (Rag/Chunking/ServiceCollectionExtensions.cs):
    // Application-side DI registers services with default options;
    // configuration-binding lives in the consuming host (CLI, Worker
    // assembly) so this method doesn't drag a
    // `Microsoft.Extensions.Configuration` dependency into Application.
    // Data-annotation validation on `RagIngestionOptions` is enforced
    // by the host that calls `services.AddOptions<RagIngestionOptions>()
    // .Bind(...).ValidateDataAnnotations().ValidateOnStart()`.
    public static IServiceCollection AddRagIngestionPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<RagIngestionOptions>();
        services.TryAddSingleton<IRagIngestionPipeline, ScrapedDocumentIngestionPipeline>();

        return services;
    }
}
