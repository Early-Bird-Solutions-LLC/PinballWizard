using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// DI registration helpers for the Pinball Brothers Freshdesk support-portal
// scraper. Separate from PinballBrothers.ServiceCollectionExtensions because
// this targets a different host (pinballbrothers.freshdesk.com, not
// pinballbrothers.com) with its own HttpClient and politeness configuration.
public static class FreshdeskScrapingExtensions
{
    public static IServiceCollection AddPinballBrothersFreshdeskScraping(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<FreshdeskOptions>()
            .Bind(configuration.GetSection(FreshdeskOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<FreshdeskSolutionsClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var opts = sp.GetRequiredService<IOptions<FreshdeskOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<PbFreshdeskDocumentScraper>();
        services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<PbFreshdeskDocumentScraper>());

        // The synthesizer (Task 7) is registered here too since it shares
        // this same HttpClient/FreshdeskSolutionsClient registration.
        services.AddTransient<PbFreshdeskArticleSynthesizer>();

        return services;
    }
}
