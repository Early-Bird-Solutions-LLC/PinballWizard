using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// DI registration helpers for Tilt Forums rulesheet ingestion.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a typed <see cref="HttpClient"/> for
    /// <see cref="TiltForumsRulesheetsClient"/> and
    /// <see cref="TiltForumsRulesheetsSynthesizer"/> as transients. No config
    /// section is needed — the base URL is a hardcoded constant on the
    /// client, matching <c>ManualsScraper</c>'s pattern, since this source
    /// has no auth and no per-environment override requirement.
    /// </summary>
    public static IServiceCollection AddTiltForumsScraping(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<TiltForumsRulesheetsClient>((sp, client) =>
        {
            var politeness = sp.GetRequiredService<IOptions<PolitenessOptions>>().Value;
            client.BaseAddress = new Uri("https://tiltforums.com");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(politeness.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<TiltForumsRulesheetsSynthesizer>();

        return services;
    }
}
