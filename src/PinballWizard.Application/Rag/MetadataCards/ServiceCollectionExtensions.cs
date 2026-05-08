using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PinballWizard.Application.Rag.MetadataCards;

public static class ServiceCollectionExtensions
{
    // Wires the Phase 4 metadata-card synthesizer (build-spec § Phase 4
    // item 17) into DI. Singleton because the synthesizer holds a
    // tokenizer instance whose load cost (~50ms BPE vocab decode from
    // the Cl100kBase data package) should happen once per process —
    // mirrors the AddHybridChunker pattern.
    //
    // No options surface today; the synthesizer is parameter-free
    // (formatting decisions are intrinsic to the customer-facing
    // citation-snippet quality bar, not config-tunable). If H3 eval
    // surfaces a need (e.g., "limit features to top N for shorter
    // cards"), introduce MetadataCardSynthesizerOptions as a sibling
    // to ChunkerOptions then.
    public static IServiceCollection AddMetadataCardSynthesizer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMetadataCardSynthesizer, MetadataCardSynthesizer>();
        return services;
    }
}
