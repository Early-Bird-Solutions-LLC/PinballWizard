namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Thrown when a politeness invariant is violated and continuing the
/// scrape would harm the source site. The orchestrator catches this
/// at the source boundary, logs it, and skips the source for the rest
/// of the run rather than retrying.
/// </summary>
/// <remarks>
/// Distinct from transient errors (which the SDK / Polly handles): a
/// PolitenessException means we have decided NOT to keep asking. The
/// source-site operator's signal (a 429 streak, a robots.txt
/// disallow, an explicit deny) is what we honor.
/// </remarks>
public sealed class PolitenessException : Exception
{
    /// <summary>The kind of politeness violation that triggered the exception.</summary>
    public PolitenessViolation Violation { get; }

    /// <summary>The URL that the request was about to be sent to (or was rejected from).</summary>
    public Uri? Url { get; }

    /// <summary>Initializes a new <see cref="PolitenessException"/>.</summary>
    public PolitenessException(PolitenessViolation violation, string message, Uri? url = null)
        : base(message)
    {
        Violation = violation;
        Url = url;
    }

    /// <summary>Initializes a new <see cref="PolitenessException"/> with an inner exception.</summary>
    public PolitenessException(PolitenessViolation violation, string message, Exception inner, Uri? url = null)
        : base(message, inner)
    {
        Violation = violation;
        Url = url;
    }
}

/// <summary>The kind of politeness violation a <see cref="PolitenessException"/> reports.</summary>
public enum PolitenessViolation
{
    /// <summary>The URL is disallowed by the host's <c>robots.txt</c> for our User-Agent.</summary>
    RobotsTxtDisallow,

    /// <summary>The source returned HTTP 429 too many times in a row; we abort to avoid worsening the situation.</summary>
    TooMany429Responses,
}
