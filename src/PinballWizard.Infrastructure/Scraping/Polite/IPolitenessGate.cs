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
    /// </list>
    /// The returned <see cref="IAsyncDisposable"/> must be disposed
    /// to release the per-origin slot. Disposing also stamps the
    /// "last request time" so the next acquire applies the delay.
    /// </summary>
    Task<IAsyncDisposable> AcquireForRequestAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Reports the response status of a recently-issued request. Used
    /// to drive the 429 streak counter:
    /// <list type="bullet">
    ///   <item>Status 200-399: streak resets.</item>
    ///   <item>Status 429: increment; if streak now exceeds the configured maximum, throws <see cref="PolitenessException"/> with <see cref="PolitenessViolation.TooMany429Responses"/>. Otherwise awaits <paramref name="retryAfter"/> (when present) before returning.</item>
    ///   <item>Other statuses: streak unchanged.</item>
    /// </list>
    /// </summary>
    Task ReportResponseAsync(Uri url, HttpStatusCode statusCode, TimeSpan? retryAfter, CancellationToken cancellationToken);
}
