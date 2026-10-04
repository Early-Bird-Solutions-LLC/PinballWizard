using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Http;

/// <summary>
/// Marks a request as sent through a pipeline that leaves HTTP 429 to the
/// politeness gate (see <see cref="PoliteHttpClientBuilderExtensions.GateOwnsRateLimit"/>).
/// Registered by <see cref="PoliteHttpClientBuilderExtensions.AddPoliteResilienceHandler"/>;
/// also usable directly over a handler that performs no retries.
/// </summary>
public sealed class GateOwnsRateLimitHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(PoliteHttpClientBuilderExtensions.GateOwnsRateLimit, true);
        return base.SendAsync(request, cancellationToken);
    }
}
