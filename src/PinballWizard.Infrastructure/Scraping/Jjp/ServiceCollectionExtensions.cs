using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Infrastructure.Scraping.Jjp;

/// <summary>
/// DI registration helpers for the JJP scraper.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the JJP scraper:
    /// <list type="bullet">
    ///   <item>Binds <see cref="JjpOptions"/> from configuration section <c>Jjp</c> with validation.</item>
    ///   <item>Registers a typed <see cref="HttpClient"/> for <see cref="JjpSitemapClient"/> and another for <see cref="JjpProductScraper"/>, each with the polite User-Agent applied.</item>
    ///   <item>Registers <see cref="JjpProductScraper"/> as both a concrete service AND as an <see cref="ISourceScraper"/> contributor — so it shows up in <c>IEnumerable&lt;ISourceScraper&gt;</c> alongside the Stern scrapers.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddJjpScraping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<JjpOptions>()
            .Bind(configuration.GetSection(JjpOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<JjpSitemapClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var jjp = sp.GetRequiredService<IOptions<JjpOptions>>().Value;

            client.BaseAddress = new Uri(jjp.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<JjpProductScraper>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var jjp = sp.GetRequiredService<IOptions<JjpOptions>>().Value;

            client.BaseAddress = new Uri(jjp.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        // Bridge the typed-client registration into the ISourceScraper enumerable.
        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<JjpProductScraper>());

        return services;
    }
}
