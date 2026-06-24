using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PinballWizard.Application.Rag.GameOverviews;

public static class ServiceCollectionExtensions
{
    // Wires the game-overview synthesizer into DI. Singleton because the
    // synthesizer holds a tokenizer instance whose load cost (~50ms BPE
    // vocab decode from the Cl100kBase data package) should happen once
    // per process — mirrors the AddMetadataCardSynthesizer pattern.
    public static IServiceCollection AddGameOverviewSynthesizer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IGameOverviewSynthesizer, GameOverviewSynthesizer>();
        return services;
    }
}
