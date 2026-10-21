using System.Net;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// The single choke point through which every scraper request to a
/// source site flows. Encapsulates the project's polite-scraping
/// invariants: per-origin throttle, robots.txt respect, 429 backoff
/// and abort-on-streak.
/// </summary>
/// <remarks>
/// Singleton-scoped — the per-origin throttle state is shared across
/// every scraper running in the process so two concurrent scrapers
/// against the same origin do not double-pace.
/// </remarks>
public interface IPolitenessGate
{
    /// <summary>
    /// Acquires a politeness lease for an upcoming request to
    /// <paramref name="url"/>. The lease enforces:
    /// <list type="bullet">
    ///   <item>Robots.txt check — throws <see cref="PolitenessException"/> with <see cref="PolitenessViolation.RobotsTxtDisallow"/> if disallowed.</item>
    ///   <item>Per-origin serialization — concurrent acquires for the same origin queue.</item>
    ///   <item>Per-origin minimum delay — the lease only completes after the configured delay since the last request to this origin has elapsed.</item>
    ///   <item>Per-origin rate-limit backoff — after a 429, the lease does not complete until the backoff recorded by <see cref="ReportResponseAsync"/> has elapsed.</item>
    /// </list>
    /// The returned <see cref="IAsyncDisposable"/> must be disposed
    /// to release the per-origin slot. Disposing also stamps the
    /// "last request time" so the next acquire applies the delay.
    /// </summary>
    Task<IAsyncDisposable> AcquireForRequestAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Reports the response status of a recently-issued request. Used
    /// to drive the per-origin 429 streak counter and backoff:
    /// <list type="bullet">
    ///   <item>Status 200-399: the origin's streak resets.</item>
    ///   <item>Status 429: increment the origin's streak; if it now exceeds the configured maximum, throws <see cref="PolitenessException"/> with <see cref="PolitenessViolation.TooMany429Responses"/>. A <paramref name="retryAfter"/> longer than the configured budget throws with <see cref="PolitenessViolation.RetryAfterExceedsBudget"/>. Otherwise records a backoff for the origin — <paramref name="retryAfter"/> when the source sent one, else an exponential policy backoff — that the next <see cref="AcquireForRequestAsync"/> for the origin waits out.</item>
    ///   <item>Other statuses: streak unchanged.</item>
    /// </list>
    /// <paramref name="retryAfter"/> is the already-resolved wait (see <see cref="RateLimitSignals.GetRetryAfter"/>, which handles both delay-seconds and HTTP-date forms).
    /// </summary>
    Task ReportResponseAsync(Uri url, HttpStatusCode statusCode, TimeSpan? retryAfter, CancellationToken cancellationToken);
}
