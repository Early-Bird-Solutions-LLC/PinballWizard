using System.Net;
using System.Net.Http.Headers;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

public sealed class RateLimitSignalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetRetryAfter_DelaySeconds_ReturnsDelta()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "120");

        Assert.Equal(TimeSpan.FromSeconds(120), RateLimitSignals.GetRetryAfter(response.Headers, Now));
    }

    [Fact]
    public void GetRetryAfter_HttpDate_MeasuredAgainstResponseDateHeader()
    {
        // The server's own Date header is the reference, so a client clock that
        // is an hour off does not turn a 90 s wait into 0 s or 61 minutes.
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Date", "Sun, 27 Sep 2026 11:00:00 GMT");
        response.Headers.TryAddWithoutValidation("Retry-After", "Sun, 27 Sep 2026 11:01:30 GMT");

        var skewedClientNow = Now + TimeSpan.FromHours(1);

        Assert.Equal(TimeSpan.FromSeconds(90), RateLimitSignals.GetRetryAfter(response.Headers, skewedClientNow));
    }

    [Fact]
    public void GetRetryAfter_HttpDateWithoutDateHeader_MeasuredAgainstNow()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Now + TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.FromSeconds(45), RateLimitSignals.GetRetryAfter(response.Headers, Now));
    }

    [Fact]
    public void GetRetryAfter_HttpDateInThePast_ReturnsZero()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Now - TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.Zero, RateLimitSignals.GetRetryAfter(response.Headers, Now));
    }

    [Fact]
    public void GetRetryAfter_Absent_ReturnsNull()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.Null(RateLimitSignals.GetRetryAfter(response.Headers, Now));
    }

    [Theory]
    [InlineData("x-vercel-mitigated", "challenge")]
    [InlineData("cf-mitigated", "challenge")]
    public void GetBotChallengeMarker_ChallengeHeader_ReturnsHeaderName(string header, string value)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation(header, value);

        Assert.Equal(header, RateLimitSignals.GetBotChallengeMarker(response.Headers));
    }

    [Fact]
    public void GetBotChallengeMarker_PlainRateLimit_ReturnsNull()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "5");

        Assert.Null(RateLimitSignals.GetBotChallengeMarker(response.Headers));
    }
}
