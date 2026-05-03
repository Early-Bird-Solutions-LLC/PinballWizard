using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// DI registration helpers for the polite-scraping foundation.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the polite-scraping foundation:
    /// <list type="bullet">
    ///   <item>Binds <see cref="PolitenessOptions"/> from configuration section <c>Politeness</c> with validation.</item>
    ///   <item>Adds a typed <see cref="HttpClient"/> for <see cref="RobotsTxtCache"/> with the polite User-Agent applied.</item>
    ///   <item>Registers <see cref="RobotsTxtCache"/> as a singleton (per-host parsed rules cached process-wide).</item>
    ///   <item>Registers <see cref="DefaultPerSourcePolitenessResolver"/> as the <see cref="IPerSourcePolitenessResolver"/> via <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService,TImplementation}(IServiceCollection)"/> — so a Cosmos-backed implementation registered later by <c>AddCosmosBackedPolitenessOverrides</c> takes precedence.</item>
    ///   <item>Registers <see cref="IPolitenessGate"/> as a singleton (per-origin throttle and 429 streak shared across all scrapers).</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddPoliteScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<PolitenessOptions>()
            .Bind(configuration.GetSection(PolitenessOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<RobotsTxtCache>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PolitenessOptions>>().Value;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.TryAddSingleton<IPerSourcePolitenessResolver, DefaultPerSourcePolitenessResolver>();
        services.AddSingleton<IPolitenessGate, PolitenessGate>();

        return services;
    }

    /// <summary>
    /// Replaces the default per-source politeness resolver with the
    /// Cosmos-backed <see cref="IngestionSourcePolitenessResolver"/>.
    /// Call this AFTER <see cref="AddPoliteScraping"/> AND after
    /// <c>AddCosmosPersistence</c> (which registers the
    /// <c>IIngestionSourceRepository</c> the resolver depends on).
    /// </summary>
    public static IServiceCollection AddCosmosBackedPolitenessOverrides(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPerSourcePolitenessResolver, IngestionSourcePolitenessResolver>();
        return services;
    }
}
