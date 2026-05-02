using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddSingleton<IPolitenessGate, PolitenessGate>();

        return services;
    }
}
