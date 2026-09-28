using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Kineticist;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

/// <summary>
/// Pins the HTTP pipeline under the Kineticist tutorials client: the host-wide
/// standard resilience handler (ServiceDefaults' ConfigureHttpClientDefaults)
/// used to re-send a 429 three times in ~14 s below the politeness gate
/// (issue #968: "Polly[3] Execution attempt. Result: '429' ... Attempt: '3'").
/// The polite pipeline must hand a 429 straight back to the gate while still
/// retrying genuinely transient failures.
/// </summary>
public sealed class PoliteHttpClientBuilderExtensionsTests
{
    [Fact]
    public async Task KineticistClientPipeline_429_IsNotRetriedBelowTheGate()
    {
        var primary = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var provider = BuildProvider(primary);

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KineticistTutorialsClient));
        using var response = await client.GetAsync(new Uri("https://www.kineticist.com/sitemap/news.xml"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, primary.Calls);
    }

    [Fact]
    public async Task KineticistClientPipeline_503_IsStillRetriedAsTransient()
    {
        var primary = new CountingHandler(n => n == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var provider = BuildProvider(primary);

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KineticistTutorialsClient));
        using var response = await client.GetAsync(new Uri("https://www.kineticist.com/sitemap/news.xml"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, primary.Calls);
    }

    private static ServiceProvider BuildProvider(CountingHandler primary)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddOptions<PolitenessOptions>();

        // What ServiceDefaults.AddServiceDefaults does for every host.
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

        services.AddKineticistScraping(new ConfigurationBuilder().Build());
        services.AddHttpClient(nameof(KineticistTutorialsClient))
            .ConfigurePrimaryHttpMessageHandler(() => primary);

        return services.BuildServiceProvider();
    }

    private sealed class CountingHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(Interlocked.Increment(ref _calls)));
    }
}
