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
    protected async Task<HttpResponseMessage> SendPolitelyAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        var url = request.RequestUri ?? throw new InvalidOperationException("Request must have a RequestUri.");

        await using var lease = await Politeness.AcquireForRequestAsync(url, cancellationToken).ConfigureAwait(false);

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await Politeness.ReportResponseAsync(url, response.StatusCode, response.Headers.RetryAfter?.Delta, cancellationToken).ConfigureAwait(false);
        return response;
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
