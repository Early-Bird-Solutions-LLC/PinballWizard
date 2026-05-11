using System.Net;
using Azure;
using Azure.Core;
using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Tests.Unit.Application.Application.Ai;

// Pins the ADR-0026 § 9 DegradationContext contract at the seam layer:
//
//  - DegradationMode enum stability (numeric values must not shift after
//    the enum is serialized to telemetry or wire-format).
//  - WizardAnswer.Degradation is null on healthy, cost-ceiling, confidence-
//    threshold, and no-citation answers.
//  - Is429 helper correctly classifies RequestFailedException (status=429)
//    and HttpRequestException (StatusCode=429).
//  - TryReadRetryAfterSeconds returns null when no Retry-After header is
//    present (caller defaults to 60s) and parses the header value when
//    present.
//
// AiRouter end-to-end integration (the full path from wizardAgent.RunAsync
// throwing → 429 catch arm → WizardAnswer) is not unit-testable here
// because AIAgent.RunAsync is not virtual and NSubstitute cannot intercept
// it. The Is429 / TryReadRetryAfterSeconds helpers are the seam exposed
// as internal via InternalsVisibleTo; the catch-arm behavior is covered by
// those helper assertions + the existing integration test surface. This is
// the same trade-off the existing AiRouterRefusalContractTests makes for
// the full guardrail paths.
public sealed class DegradationContextTests
{
    // ────────────────────────────────────────────────────────────────────
    // DegradationMode enum stability
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DegradationMode_None_IsValueZero()
    {
        Assert.Equal(0, (int)DegradationMode.None);
    }

    [Fact]
    public void DegradationMode_SearchUnavailable_IsValueOne()
    {
        Assert.Equal(1, (int)DegradationMode.SearchUnavailable);
    }

    [Fact]
    public void DegradationMode_UpstreamThrottled_IsValueTwo()
    {
        Assert.Equal(2, (int)DegradationMode.UpstreamThrottled);
    }

    [Fact]
    public void DegradationMode_PartialResults_IsValueThree()
    {
        Assert.Equal(3, (int)DegradationMode.PartialResults);
    }

    // ────────────────────────────────────────────────────────────────────
    // RefusalCategory.UpstreamThrottled enum stability
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalCategory_UpstreamThrottled_IsValueSix()
    {
        // Pinned because telemetry consumers serialize enum-as-int when
        // minimizing payloads; a reorder would shift the value silently.
        Assert.Equal(6, (int)RefusalCategory.UpstreamThrottled);
    }

    // ────────────────────────────────────────────────────────────────────
    // WizardAnswer.Degradation is null on non-degraded paths
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void WizardAnswer_HealthyAnswer_DegradationIsNull()
    {
        var answer = new WizardAnswer(
            Text: "A healthy answer.",
            Citations: [new Citation("Title", "https://example.com")],
            SubAgentUsed: AgentName.Wizard,
            Confidence: 0.9,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: "v1.test",
            FoundryThreadId: null);

        Assert.Null(answer.Degradation);
    }

    [Fact]
    public void WizardAnswer_CostCeilingRefusal_DegradationIsNull()
    {
        var answer = new WizardAnswer(
            Text: AiRouter.BuildRefusalText(RefusalCategory.CostCeilingHit),
            Citations: [],
            SubAgentUsed: AgentName.Wizard,
            Confidence: 0.0,
            Escalated: false,
            IsRefusal: true,
            RefusalCategory: RefusalCategory.CostCeilingHit,
            PromptVersion: "v1.test",
            FoundryThreadId: null);

        Assert.Null(answer.Degradation);
    }

    [Fact]
    public void WizardAnswer_ConfidenceThresholdRefusal_DegradationIsNull()
    {
        var answer = new WizardAnswer(
            Text: AiRouter.BuildRefusalText(RefusalCategory.InsufficientGrounding),
            Citations: [],
            SubAgentUsed: AgentName.Wizard,
            Confidence: 0.3,
            Escalated: false,
            IsRefusal: true,
            RefusalCategory: RefusalCategory.InsufficientGrounding,
            PromptVersion: "v1.test",
            FoundryThreadId: null);

        Assert.Null(answer.Degradation);
    }

    [Fact]
    public void WizardAnswer_NoCitationRefusal_DegradationIsNull()
    {
        var answer = new WizardAnswer(
            Text: AiRouter.BuildRefusalText(RefusalCategory.NoCitation),
            Citations: [],
            SubAgentUsed: AgentName.Wizard,
            Confidence: 0.7,
            Escalated: false,
            IsRefusal: true,
            RefusalCategory: RefusalCategory.NoCitation,
            PromptVersion: "v1.test",
            FoundryThreadId: null);

        Assert.Null(answer.Degradation);
    }

    // ────────────────────────────────────────────────────────────────────
    // WizardAnswer.Degradation is populated on UpstreamThrottled
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void WizardAnswer_UpstreamThrottledRefusal_DegradationPopulated()
    {
        var degradation = new DegradationContext(
            Mode: DegradationMode.UpstreamThrottled,
            Detail: "Upstream model rate-limited the request.",
            RetryAfterSeconds: 60);

        var answer = new WizardAnswer(
            Text: AiRouter.BuildRefusalText(RefusalCategory.UpstreamThrottled),
            Citations: [],
            SubAgentUsed: AgentName.Wizard,
            Confidence: 0.0,
            Escalated: false,
            IsRefusal: true,
            RefusalCategory: RefusalCategory.UpstreamThrottled,
            PromptVersion: "v1.test",
            FoundryThreadId: null,
            Degradation: degradation);

        Assert.True(answer.IsRefusal);
        Assert.Equal(RefusalCategory.UpstreamThrottled, answer.RefusalCategory);
        Assert.NotNull(answer.Degradation);
        Assert.Equal(DegradationMode.UpstreamThrottled, answer.Degradation.Mode);
        Assert.Equal(60, answer.Degradation.RetryAfterSeconds);
    }

    // ────────────────────────────────────────────────────────────────────
    // BuildRefusalText for UpstreamThrottled
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildRefusalText_UpstreamThrottled_StartsWithIDontKnow()
    {
        var text = AiRouter.BuildRefusalText(RefusalCategory.UpstreamThrottled);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.StartsWith("I don't know", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRefusalText_UpstreamThrottled_IsDistinctFromAllOtherCategories()
    {
        var throttledText = AiRouter.BuildRefusalText(RefusalCategory.UpstreamThrottled);

        var otherTexts = new[]
        {
            AiRouter.BuildRefusalText(RefusalCategory.InsufficientGrounding),
            AiRouter.BuildRefusalText(RefusalCategory.OutOfScope),
            AiRouter.BuildRefusalText(RefusalCategory.LowModelConfidence),
            AiRouter.BuildRefusalText(RefusalCategory.CostCeilingHit),
            AiRouter.BuildRefusalText(RefusalCategory.HarmfulContent),
            AiRouter.BuildRefusalText(RefusalCategory.NoCitation),
        };

        Assert.DoesNotContain(throttledText, otherTexts, StringComparer.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────
    // Is429 helper — classifies exception types correctly
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Is429_RequestFailedException_Status429_ReturnsTrue()
    {
        var ex = new RequestFailedException(429, "Too Many Requests");

        Assert.True(AiRouter.Is429(ex));
    }

    [Fact]
    public void Is429_RequestFailedException_Status500_ReturnsFalse()
    {
        var ex = new RequestFailedException(500, "Internal Server Error");

        Assert.False(AiRouter.Is429(ex));
    }

    [Fact]
    public void Is429_HttpRequestException_Status429_ReturnsTrue()
    {
        var ex = new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);

        Assert.True(AiRouter.Is429(ex));
    }

    [Fact]
    public void Is429_HttpRequestException_Status503_ReturnsFalse()
    {
        var ex = new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable);

        Assert.False(AiRouter.Is429(ex));
    }

    [Fact]
    public void Is429_GenericException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Something went wrong");

        Assert.False(AiRouter.Is429(ex));
    }

    [Fact]
    public void Is429_OperationCanceledException_ReturnsFalse()
    {
        var ex = new OperationCanceledException("Cancelled");

        Assert.False(AiRouter.Is429(ex));
    }

    // ────────────────────────────────────────────────────────────────────
    // TryReadRetryAfterSeconds — header extraction
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryReadRetryAfterSeconds_RequestFailedExceptionNoResponse_ReturnsNull()
    {
        // RequestFailedException(int, string) stores no raw response —
        // GetRawResponse() returns null. The caller falls back to 60s.
        var ex = new RequestFailedException(429, "Too Many Requests");

        var result = AiRouter.TryReadRetryAfterSeconds(ex);

        Assert.Null(result);
    }

    [Fact]
    public void TryReadRetryAfterSeconds_RequestFailedExceptionWithRetryAfterHeader_ReturnsSeconds()
    {
        // Construct a RequestFailedException backed by a fake Response that
        // carries a Retry-After: 30 header. This exercises the full header-
        // extraction path of TryReadRetryAfterSeconds.
        var fakeResponse = new FakeResponse(429, new Dictionary<string, string>
        {
            ["Retry-After"] = "30",
        });
        var ex = new RequestFailedException(fakeResponse);

        var result = AiRouter.TryReadRetryAfterSeconds(ex);

        Assert.Equal(30, result);
    }

    [Fact]
    public void TryReadRetryAfterSeconds_RequestFailedExceptionWithNonNumericRetryAfter_ReturnsNull()
    {
        // Non-integer Retry-After values (e.g., HTTP-date format) are not
        // currently parsed; the caller defaults to 60s. This pins the
        // "graceful null on unparseable header" contract.
        var fakeResponse = new FakeResponse(429, new Dictionary<string, string>
        {
            ["Retry-After"] = "Wed, 21 Oct 2026 07:28:00 GMT",
        });
        var ex = new RequestFailedException(fakeResponse);

        var result = AiRouter.TryReadRetryAfterSeconds(ex);

        Assert.Null(result);
    }

    [Fact]
    public void TryReadRetryAfterSeconds_HttpRequestException_ReturnsNull()
    {
        // HttpRequestException does not carry response headers at the
        // exception level; always returns null regardless of status code.
        var ex = new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);

        var result = AiRouter.TryReadRetryAfterSeconds(ex);

        Assert.Null(result);
    }

    [Fact]
    public void TryReadRetryAfterSeconds_GenericException_ReturnsNull()
    {
        var ex = new InvalidOperationException("boom");

        var result = AiRouter.TryReadRetryAfterSeconds(ex);

        Assert.Null(result);
    }

    // ────────────────────────────────────────────────────────────────────
    // DegradationContext record semantics
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DegradationContext_EqualsByValue()
    {
        var a = new DegradationContext(DegradationMode.UpstreamThrottled, "detail", 60);
        var b = new DegradationContext(DegradationMode.UpstreamThrottled, "detail", 60);

        Assert.Equal(a, b);
    }

    [Fact]
    public void DegradationContext_NullDetail_IsPermitted()
    {
        var ctx = new DegradationContext(DegradationMode.SearchUnavailable, null, null);

        Assert.Null(ctx.Detail);
        Assert.Null(ctx.RetryAfterSeconds);
    }

    // ────────────────────────────────────────────────────────────────────
    // Minimal Azure.Core.Response subclass for testing header extraction
    // ────────────────────────────────────────────────────────────────────

    // Inline fake so the test project does not depend on
    // Azure.Core.TestFramework (an additional package that would need
    // Directory.Packages.props entry). The only purpose is to supply a
    // Response with controllable headers to RequestFailedException(Response).
    private sealed class FakeResponse : Response
    {
        private readonly int _status;
        private readonly Dictionary<string, string> _headers;

        public FakeResponse(int status, Dictionary<string, string> headers)
        {
            _status = status;
            _headers = headers;
        }

        public override int Status => _status;
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose() { }

        protected override bool TryGetHeader(
            string name,
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value)
            => _headers.TryGetValue(name, out value!);

        protected override bool TryGetHeaderValues(
            string name,
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IEnumerable<string> values)
        {
            if (_headers.TryGetValue(name, out var v))
            {
                values = [v];
                return true;
            }

            values = null!;
            return false;
        }

        protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
            => _headers.Select(kv => new HttpHeader(kv.Key, kv.Value));
    }
}
