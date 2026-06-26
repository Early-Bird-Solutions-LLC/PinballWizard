using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.Kineticist;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Kineticist;

/// <summary>
/// DI registration helpers for the Kineticist tutorials scraper.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Kineticist tutorials client and synthesizer:
    /// <list type="bullet">
    ///   <item>Binds <see cref="KineticistOptions"/> from configuration section <c>Kineticist</c> with validation.</item>
    ///   <item>Registers a typed <see cref="System.Net.Http.HttpClient"/> for <see cref="KineticistTutorialsClient"/>.</item>
    ///   <item>Registers <see cref="KineticistTutorialsClient"/> and <see cref="KineticistTutorialsSynthesizer"/> as singletons.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddKineticistScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<KineticistOptions>()
            .Bind(configuration.GetSection(KineticistOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<KineticistTutorialsClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var opts = sp.GetRequiredService<IOptions<KineticistOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/plain, text/markdown, text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<KineticistTutorialsSynthesizer>();

        // ADR-0043 Tier A — OPDB-keyed API linking. The typed client routes
        // through the politeness gate like every other integration; auth is
        // applied per-request from KineticistOptions.ApiKey. The resolver is
        // registered unconditionally (it is cheap and stateless); the tutorials
        // sync only invokes it when an API key is configured, otherwise it falls
        // back to title-lookup linking.
        services.AddHttpClient<KineticistApiClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddTransient<IKineticistApiClient>(sp => sp.GetRequiredService<KineticistApiClient>());
        services.AddTransient<IKineticistGameResolver, KineticistGameResolver>();

        return services;
    }
}
