using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Resilience wiring for typed <see cref="HttpClient"/>s whose requests route
/// through <see cref="IPolitenessGate"/>.
/// </summary>
public static class PoliteHttpClientBuilderExtensions
{
    /// <summary>
    /// Replaces any inherited resilience pipeline (e.g. the host-wide
    /// <c>ConfigureHttpClientDefaults</c> standard handler) with a standard
    /// handler that retries transient failures (5xx, 408, network errors,
    /// attempt timeouts) but never HTTP 429.
    /// </summary>
    /// <remarks>
    /// A 429 is the source operator telling us to slow down. The inherited
    /// pipeline re-sends it within seconds, below the politeness gate, where the
    /// gate cannot pace or count the attempts — the gate only ever saw the last
    /// one. Leaving 429 to the gate means every refusal is reported, the
    /// source's <c>Retry-After</c> (or the policy backoff) is waited out for the
    /// whole origin, and the per-source 429 budget bounds the retries.
    /// </remarks>
    /// <param name="builder">The typed client builder.</param>
    /// <param name="attemptTimeout">Per-attempt timeout.</param>
    /// <param name="totalTimeout">Total timeout across transient retries; keep it within <see cref="HttpClient.Timeout"/>.</param>
    public static IHttpClientBuilder AddPoliteResilienceHandler(
        this IHttpClientBuilder builder,
        TimeSpan attemptTimeout,
        TimeSpan totalTimeout)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // EXTEXP0001: RemoveAllResilienceHandlers is marked experimental but is the
        // supported opt-out from ConfigureHttpClientDefaults pipelines (same use as
        // the Web project's WizardStreamingClient).
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = attemptTimeout;
            options.TotalRequestTimeout.Timeout = totalTimeout;
            // HttpStandardResilienceOptions.Validate requires SamplingDuration >= 2 * AttemptTimeout.
            options.CircuitBreaker.SamplingDuration = attemptTimeout * 2;
            options.Retry.ShouldHandle = args => ValueTask.FromResult(IsTransientExceptRateLimit(args.Outcome));
        });

        return builder;
    }

    internal static bool IsTransientExceptRateLimit(Outcome<HttpResponseMessage> outcome) =>
        HttpClientResiliencePredicates.IsTransient(outcome)
        && outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests;
}
