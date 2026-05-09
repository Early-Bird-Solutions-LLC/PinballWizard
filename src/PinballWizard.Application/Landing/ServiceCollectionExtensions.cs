using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PinballWizard.Application.Landing;

public static class ServiceCollectionExtensions
{
    // Wires the Landing surface (ADR-0026 § Landing surface) into DI.
    // Services are singletons: the seed-question JSON is static between
    // deploys, and LandingService has no mutable state.
    //
    // PR-L3: ISystemStatusProvider is registered by the Infrastructure DI
    // extension (AddSystemStatusProvider in the Landing DI helpers) because
    // the implementation (SystemStatusProvider) depends on Infrastructure
    // types (IAzureFoundrySmokeProbe, IAzureAiSearchSmokeProbe,
    // ICosmosCanaryProbe). IOptions<SystemStatusOptions> is bound to the
    // "Landing:SystemStatus" configuration section by AddSystemStatusProvider;
    // the consuming host calls both AddLandingService and
    // AddSystemStatusProvider. ILandingService degrades gracefully to
    // SystemStatus=null when ISystemStatusProvider is absent (optional dep).
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
