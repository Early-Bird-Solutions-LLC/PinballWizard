using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Twip;

// DI registration helpers for the TWIP newsletter scraper.
public static class TwipScrapingExtensions
{
    // Registers the TWIP newsletter client and synthesizer:
    // - Binds TwipOptions from configuration section "Twip" with validation.
    // - Registers a typed HttpClient for TwipNewsletterClient.
    // - Registers TwipNewsletterSynthesizer as a transient.
    public static IServiceCollection AddTwipScraping(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<TwipOptions>()
            .Bind(configuration.GetSection(TwipOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<TwipNewsletterClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            var opts = sp.GetRequiredService<IOptions<TwipOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddTransient<TwipNewsletterSynthesizer>();

        return services;
    }
}
