using System.Net.Http.Headers;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Reads the rate-limit and bot-protection signals a source sends back on
/// a response, so the politeness gate and <see cref="PoliteScraperBase"/>
/// act on what the operator actually asked for.
/// </summary>
public static class RateLimitSignals
{
    // Documented mitigation headers: Vercel Firewall sets
    // "x-vercel-mitigated: challenge" on its Security Checkpoint (served as 429);
    // Cloudflare sets "cf-mitigated: challenge" on managed challenges (served as 403).
    private static readonly string[] ChallengeHeaders = ["x-vercel-mitigated", "cf-mitigated"];

    /// <summary>
    /// Returns the wait a <c>Retry-After</c> header asks for, or
    /// <see langword="null"/> when the header is absent. Handles both RFC 9110
    /// forms: delay-seconds, and an HTTP-date. An HTTP-date is measured against
    /// the response's own <c>Date</c> header when present (so client/server clock
    /// skew cancels out), otherwise against <paramref name="now"/>. A date in the
    /// past yields <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public static TimeSpan? GetRetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var retryAfter = headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var reference = headers.Date ?? now;
            var wait = date - reference;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>
    /// Returns the name of the bot-protection header that marks this response
    /// as a challenge page, or <see langword="null"/> when it is not one. A
    /// challenge is not a rate limit: waiting and retrying cannot pass it.
    /// </summary>
    public static string? GetBotChallengeMarker(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var name in ChallengeHeaders)
        {
            if (headers.TryGetValues(name, out var values)
                && values.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }

        return null;
    }
}
