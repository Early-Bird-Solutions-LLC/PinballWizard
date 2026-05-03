using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Infrastructure.Scraping.ChicagoGaming;

/// <summary>
/// DI registration helpers for the Chicago Gaming Company scraper.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the CGC scraper:
    /// <list type="bullet">
    ///   <item>Binds <see cref="ChicagoGamingOptions"/> from configuration section <c>ChicagoGaming</c> with validation.</item>
    ///   <item>Registers typed <see cref="HttpClient"/>s for <see cref="CgcMenuClient"/> and <see cref="CgcGamePageScraper"/> with the polite User-Agent.</item>
    ///   <item>Bridges the typed-client registration into the <see cref="ISourceScraper"/> enumerable.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddChicagoGamingScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<ChicagoGamingOptions>()
            .Bind(configuration.GetSection(ChicagoGamingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<CgcMenuClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var cgc = sp.GetRequiredService<IOptions<ChicagoGamingOptions>>().Value;
            client.BaseAddress = new Uri(cgc.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<CgcGamePageScraper>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var cgc = sp.GetRequiredService<IOptions<ChicagoGamingOptions>>().Value;
            client.BaseAddress = new Uri(cgc.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<CgcGamePageScraper>());

        return services;
    }
}
