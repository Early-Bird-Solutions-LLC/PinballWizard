using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Findability;

namespace PinballWizard.Infrastructure.Findability;

public static class FindabilityServiceCollectionExtensions
{
    // Registers IMachineSuggestService unconditionally (ADR-0049 phase 3).
    //
    // MachineSuggestService takes IMachineSearchIndex? with a default of null, so
    // .NET DI injects null when AI Search is not configured — the service returns
    // empty rather than failing. Consuming hosts (PinballWizard.Api) call this
    // alongside AddLandingService and AddHostCosmosPersistence; no gate required.
    public static IServiceCollection AddMachineSuggestService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMachineSuggestService, MachineSuggestService>();
        return services;
    }
}
