using System.Net;
using Microsoft.Extensions.Logging;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Base class for HTTP-driven scrapers that want to honor the project's
/// politeness invariants without re-implementing them per scraper. Per
/// the locked feedback memory <c>feedback_polite_scraping.md</c>, the
/// politeness invariants must be VISIBLY enforced — not relied on by
/// convention. Extending this base is the visible enforcement.
/// </summary>
/// <remarks>
/// Subclasses receive an <see cref="HttpClient"/> via their own typed
/// DI registration (so they get configured retry / timeout / UA from
/// the central HTTP pipeline) and the shared
/// <see cref="IPolitenessGate"/>. They MUST route every outbound
/// request through <see cref="SendPolitelyAsync"/> rather than
/// calling <see cref="HttpClient.SendAsync(HttpRequestMessage, CancellationToken)"/>
/// directly.
/// <para>
/// For convenience, <see cref="GetStringPolitelyAsync"/> covers the
/// common GET-and-read-string pattern.
/// </para>
/// </remarks>
public abstract class PoliteScraperBase
{
    /// <summary>The politeness gate this scraper routes requests through.</summary>
    protected IPolitenessGate Politeness { get; }

    /// <summary>Politeness configuration (User-Agent, delays, robots policy).</summary>
    protected PolitenessOptions PolitenessOptions { get; }

    /// <summary>Logger for use by derived classes.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Initializes a new <see cref="PoliteScraperBase"/>.
    /// </summary>
    protected PoliteScraperBase(
        IPolitenessGate politeness,
        PolitenessOptions politenessOptions,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(politeness);
        ArgumentNullException.ThrowIfNull(politenessOptions);
        ArgumentNullException.ThrowIfNull(logger);
        Politeness = politeness;
        PolitenessOptions = politenessOptions;
        Logger = logger;
    }

    /// <summary>
    /// Sends an HTTP request through the politeness gate. The gate
    /// enforces robots.txt, per-origin throttle, and 429 backoff;
    /// transient retries (5xx, network errors) are handled by the
    /// <see cref="HttpClient"/>'s configured resilience pipeline.
    /// </summary>
    /// <remarks>
    /// A 429 is always reported to the gate. A bot-protection challenge
    /// (see <see cref="RateLimitSignals.GetBotChallengeMarker"/>) throws
    /// <see cref="PolitenessException"/> with <see cref="PolitenessViolation.BotChallenge"/>
    /// immediately — no retry can pass it. Any other 429 on a request without
    /// a body, sent through a pipeline stamped
    /// <see cref="PoliteHttpClientBuilderExtensions.GateOwnsRateLimit"/>, is
    /// re-sent through a fresh lease, which waits out the backoff the gate
    /// recorded (the source's <c>Retry-After</c>, or the policy backoff); the
    /// gate's per-origin streak limit is the retry budget and throws once
    /// exhausted. Otherwise the 429 is returned to the caller: a request with a
    /// body is not safely repeatable, and an unstamped pipeline may already have
    /// retried the 429 below the gate.
    /// </remarks>
    protected Task<HttpResponseMessage> SendPolitelyAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        SendPolitelyAsync(client, request, HttpCompletionOption.ResponseContentRead, cancellationToken);

    /// <summary>
    /// Same as <see cref="SendPolitelyAsync(HttpClient, HttpRequestMessage, CancellationToken)"/>
    /// but the caller chooses when the response body is buffered.
    /// Document downloads pass <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// so a large PDF is not buffered twice (once by <see cref="HttpClient"/>,
    /// again by the caller). Page scrapers keep the default
    /// <see cref="HttpCompletionOption.ResponseContentRead"/>.
    /// </summary>
    protected async Task<HttpResponseMessage> SendPolitelyAsync(
        HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        var url = request.RequestUri ?? throw new InvalidOperationException("Request must have a RequestUri.");

        var current = request;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var response = await SendOnceAsync(client, current, url, completionOption, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.TooManyRequests
                    || request.Content is not null
                    || !GateOwnsRateLimit(current))
                {
                    return response;
                }

                response.Dispose();

                // The gate's per-origin streak limit normally throws first; this
                // cap only guarantees termination if the streak is reset concurrently.
                if (attempt > PolitenessOptions.Max429StreakUpperBound)
                {
                    throw new PolitenessException(
                        PolitenessViolation.TooMany429Responses,
                        $"Source {url.Host} kept returning 429 for {url} across {attempt} attempts. Aborting.",
                        url);
                }

                Logger.LogInformation(
                    "429 from {Url} (attempt {Attempt}); re-sending after the politeness gate's backoff.",
                    url, attempt);

                if (!ReferenceEquals(current, request))
                {
                    current.Dispose();
                }
                current = CloneWithoutContent(request);
            }
        }
        finally
        {
            if (!ReferenceEquals(current, request))
            {
                current.Dispose();
            }
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpClient client,
        HttpRequestMessage request,
        Uri url,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        await using var lease = await Politeness.AcquireForRequestAsync(url, cancellationToken).ConfigureAwait(false);

        var response = await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        try
        {
            var retryAfter = RateLimitSignals.GetRetryAfter(response.Headers, DateTimeOffset.UtcNow);
            await Politeness.ReportResponseAsync(url, response.StatusCode, retryAfter, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && RateLimitSignals.GetBotChallengeMarker(response.Headers) is { } marker)
            {
                Logger.LogError(
                    "{Host} answered {Url} with a bot-protection challenge (HTTP {Status}, {Marker}: challenge). " +
                    "Not retrying: a challenge cannot be waited out. Ask the site operator to allow this crawler's User-Agent.",
                    url.Host, url, (int)response.StatusCode, marker);
                throw new PolitenessException(
                    PolitenessViolation.BotChallenge,
                    $"{url.Host} answered {url} with a bot-protection challenge (HTTP {(int)response.StatusCode}, " +
                    $"{marker}: challenge). Retrying cannot pass it; the site operator must allow this crawler.",
                    url);
            }

            return response;
        }
        catch
        {
            // ReportResponseAsync can throw (429-streak abort), as can the
            // challenge check. The caller never receives the message in that
            // case, so this method owns disposal. ResponseHeadersRead leaves the
            // connection checked out until the content is disposed; dropping the
            // message on the floor pins it.
            response.Dispose();
            throw;
        }
    }

    private static bool GateOwnsRateLimit(HttpRequestMessage sent) =>
        sent.Options.TryGetValue(PoliteHttpClientBuilderExtensions.GateOwnsRateLimit, out var owned) && owned;

    private static HttpRequestMessage CloneWithoutContent(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var option in original.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }
        return clone;
    }

    /// <summary>
    /// Convenience over <see cref="SendPolitelyAsync"/> for a simple
    /// GET that returns the response body as a string. Throws on
    /// non-success status codes (matches <see cref="HttpClient.GetStringAsync(Uri,CancellationToken)"/>).
    /// </summary>
    protected async Task<string> GetStringPolitelyAsync(
        HttpClient client,
        Uri url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendPolitelyAsync(client, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
