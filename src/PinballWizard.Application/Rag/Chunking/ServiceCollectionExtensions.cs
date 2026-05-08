using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PinballWizard.Application.Rag.Chunking;

public static class ServiceCollectionExtensions
{
    // Wires the Phase 4 hybrid chunker (ADR-0019) into DI. Singleton
    // because HybridChunker holds a tokenizer instance whose load cost
    // (~50ms BPE vocab decode from the Cl100kBase data package) should
    // happen once per process. The tokenizer itself is thread-safe.
    //
    // Mirrors the AddAiRouter precedent: Application-side DI registers
    // services with default options; configuration-binding lives in
    // the consuming host (CLI, Functions, AppHost) so this method
    // doesn't drag a Microsoft.Extensions.Configuration dependency
    // into Application. Callers wanting non-default settings call
    // `services.Configure<ChunkerOptions>(config.GetSection(...))`
    // before this method.
    public static IServiceCollection AddHybridChunker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Data-annotation validation lives in the consuming host's DI
        // registration where Microsoft.Extensions.Options.DataAnnotations
        // is already referenced — keeping it out of Application avoids
        // pulling that package in for the chunker alone. The [Range]
        // attributes on ChunkerOptions remain as IDE documentation.
        services.AddOptions<ChunkerOptions>();
        services.TryAddSingleton<IChunker, HybridChunker>();
        return services;
    }
}
