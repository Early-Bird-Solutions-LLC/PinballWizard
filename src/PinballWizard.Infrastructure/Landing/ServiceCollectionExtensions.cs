using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Landing;

namespace PinballWizard.Infrastructure.Landing;

/// <summary>
/// DI registration for the Landing infrastructure layer (PR-L3).
/// Registers ISystemStatusProvider (singleton, stampede-safe IMemoryCache
/// based caching) and ICosmosCanaryProbe alongside the SystemStatusOptions
/// configuration binding.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires <see cref="ISystemStatusProvider"/> into DI.
    /// <list type="bullet">
    ///   <item>Binds <see cref="SystemStatusOptions"/> from the
    ///   <c>Landing:SystemStatus</c> configuration section.</item>
    ///   <item>Registers <see cref="IMemoryCache"/> (via AddMemoryCache —
    ///   idempotent, safe to call multiple times).</item>
    ///   <item>Registers <see cref="ISystemStatusProvider"/> as
    ///   <see cref="SystemStatusProvider"/> (singleton). The smoke probes
    ///   (IAzureFoundrySmokeProbe, IAzureAiSearchSmokeProbe,
    ///   ICosmosCanaryProbe) are optional dependencies in DI — absent when
    ///   the corresponding integration is not configured, causing the
    ///   matching SystemStatus field to be null ("unknown").</item>
    /// </list>
    /// <para>
    /// <see cref="ICosmosCanaryProbe"/> (<see cref="CosmosCanaryProbe"/>)
    /// is registered by <c>AddCosmosPersistence</c> (which has the
    /// CosmosClient available) to avoid a DI wiring issue when Cosmos is
    /// absent: CosmosCanaryProbe requires CosmosClient, and registering it
    /// here would fail at resolve-time if Cosmos is not configured.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSystemStatusProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SystemStatusOptions>()
            .Bind(configuration.GetSection(SystemStatusOptions.SectionName))
            .ValidateDataAnnotations();

        // IMemoryCache: AddMemoryCache is idempotent — calling it more
        // than once is harmless. SystemStatusProvider requires it for
        // stampede-safe TTL caching.
        services.AddMemoryCache();

        // SystemStatusProvider: singleton so a single IMemoryCache entry
        // is shared across all callers. Optional probe deps degrade gracefully.
        services.TryAddSingleton<ISystemStatusProvider, SystemStatusProvider>();

        return services;
    }
}
