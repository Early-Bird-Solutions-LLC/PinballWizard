using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Pricing;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.SilverballLabs;

// DI registration for the Silverball Labs live-pricing integration (ADR-0045).
//
// Call site (Cli/Program.cs, Api, Web) gates this on ApiKeyKey presence:
//   if (configuration[SilverballLabsOptions.ApiKeyKey] is not null)
//       services.AddSilverballLabsIntegration(configuration);
// When absent, IMarketValueProvider is not registered and the getMarketValue
// tool degrades gracefully to a no-pricing answer.
public static class ServiceCollectionExtensions
{
    // Registers the Silverball Labs integration:
    //   - Binds and validates SilverballLabsOptions.
    //   - Registers a typed HttpClient for SilverballLabsClient with the API key
    //     header, Accept header, and per-request timeout applied.
    //   - Registers SilverballMarketValueProvider as IMarketValueProvider singleton.
    public static IServiceCollection AddSilverballLabsIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SilverballLabsOptions>()
            .Bind(configuration.GetSection(SilverballLabsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<SilverballLabsClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<SilverballLabsOptions>>().Value;

            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

            // Auth header: Silverball Labs uses X-API-Key (ADR-0045).
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
            }

            client.DefaultRequestHeaders.Add("Accept", "application/json");

            // Polite user-agent — read PolitenessOptions if available; fall back
            // to a sensible default. SilverballLabsClient is not a PoliteScraperBase
            // (it's a partner API, not web scraping), but a courtesy user-agent
            // is always appropriate.
            if (sp.GetService<IOptions<PolitenessOptions>>()?.Value?.UserAgent is { Length: > 0 } ua)
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            }
            else
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PinballWizard/1.0");
            }

            client.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
        });

        // Register the concrete client as its interface so the provider (and tests)
        // can depend on ISilverballLabsClient rather than the sealed concrete type.
        services.AddSingleton<ISilverballLabsClient>(sp =>
            sp.GetRequiredService<SilverballLabsClient>());

        services.AddSingleton<IMarketValueProvider, SilverballMarketValueProvider>();

        return services;
    }
}
