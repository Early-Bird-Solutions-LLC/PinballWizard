using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Infrastructure.Scraping.Ap;

/// <summary>
/// DI registration helpers for the American Pinball scraper.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AP scraper:
    /// <list type="bullet">
    ///   <item>Binds <see cref="ApOptions"/> from configuration section <c>Ap</c> with validation.</item>
    ///   <item>Registers typed <see cref="HttpClient"/>s for <see cref="ApSitemapClient"/> and <see cref="ApGamePageScraper"/> with the polite User-Agent.</item>
    ///   <item>Bridges the typed-client registration into the <see cref="ISourceScraper"/> enumerable.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddAmericanPinballScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<ApOptions>()
            .Bind(configuration.GetSection(ApOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<ApSitemapClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var ap = sp.GetRequiredService<IOptions<ApOptions>>().Value;
            client.BaseAddress = new Uri(ap.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<ApGamePageScraper>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var ap = sp.GetRequiredService<IOptions<ApOptions>>().Value;
            client.BaseAddress = new Uri(ap.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<ApGamePageScraper>());

        services.AddHttpClient<ApBulletinScraper>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var ap = sp.GetRequiredService<IOptions<ApOptions>>().Value;
            client.BaseAddress = new Uri(ap.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<ApBulletinScraper>());

        return services;
    }
}
