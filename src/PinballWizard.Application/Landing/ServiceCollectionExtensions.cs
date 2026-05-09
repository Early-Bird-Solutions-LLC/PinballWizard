using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PinballWizard.Application.Landing;

public static class ServiceCollectionExtensions
{
    // Wires the Landing surface (ADR-0026 § Landing surface) into DI.
    // Both services are singletons: the seed-question JSON is static
    // between deploys, and LandingService has no mutable state.
    //
    // Mirrors AddAiRouter / AddHybridChunker precedent: Application-side
    // DI registers the services; consuming hosts (PinballWizard.Api, CLI)
    // call this method as part of their host startup.
    public static IServiceCollection AddLandingService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISeedQuestionLoader, SeedQuestionLoader>();
        services.TryAddSingleton<IFeaturedMachineSeedLoader, FeaturedMachineSeedLoader>();
        services.TryAddSingleton<ILandingService, LandingService>();

        return services;
    }
}
