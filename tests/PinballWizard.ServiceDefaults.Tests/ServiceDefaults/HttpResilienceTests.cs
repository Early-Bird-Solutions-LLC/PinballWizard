using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Xunit;

namespace PinballWizard.ServiceDefaults.Tests.ServiceDefaults;

/// <summary>
/// Verifies the HTTP resilience pipeline configuration applied by
/// <see cref="Extensions.AddServiceDefaults"/>.
///
/// The production code sets specific timeout values that are deliberately
/// larger than the Microsoft defaults to accommodate slow upstream sources
/// (OPDB bulk export ~30 s cold, Stern Vue.js pages 15-25 s).  These tests
/// guard against a silent revert to the tighter defaults, which would cause
/// flaky scraper timeouts without a test failure.
///
/// Options are registered under the pipeline name "-standard" (the
/// concatenation of the empty default-client name and "-standard" suffix
/// used by Microsoft.Extensions.Http.Resilience when a handler is added via
/// ConfigureHttpClientDefaults).
/// </summary>
public sealed class HttpResilienceTests
{
    // The options name derived from ConfigureHttpClientDefaults + AddStandardResilienceHandler:
    // pipeline name = "<clientName>-standard" where clientName="" for ConfigureHttpClientDefaults.
    private const string PipelineOptionsName = "-standard";

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddServiceDefaults();
        return builder.Build();
    }

    // ─────────────────────────────────────────────────────────────────────
    // TotalRequestTimeout — 120 s (default is 30 s)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_StandardResiliencePipeline_TotalTimeoutIs120Seconds()
    {
        using var app = BuildApp();

        var options = ResolveResilienceOptions(app);

        Assert.Equal(TimeSpan.FromSeconds(120), options.TotalRequestTimeout.Timeout);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AttemptTimeout — 50 s (default is 10 s)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_StandardResiliencePipeline_AttemptTimeoutIs50Seconds()
    {
        using var app = BuildApp();

        var options = ResolveResilienceOptions(app);

        Assert.Equal(TimeSpan.FromSeconds(50), options.AttemptTimeout.Timeout);
    }

    // ─────────────────────────────────────────────────────────────────────
    // CircuitBreaker.SamplingDuration — 120 s
    //
    // The HttpStandardResilienceOptions validator requires SamplingDuration
    // >= 2 * AttemptTimeout.  The default (30 s) would fail validation when
    // AttemptTimeout > 15 s, so this is explicitly set.  Regressing it to a
    // value < 100 s would cause host startup validation errors in production.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_StandardResiliencePipeline_CircuitBreakerSamplingIs120Seconds()
    {
        using var app = BuildApp();

        var options = ResolveResilienceOptions(app);

        Assert.Equal(TimeSpan.FromSeconds(120), options.CircuitBreaker.SamplingDuration);
    }

    // ─────────────────────────────────────────────────────────────────────
    // SamplingDuration invariant: must be >= 2 * AttemptTimeout.
    // Asserts the invariant directly so the constraint is self-documenting
    // even if individual values change via a future PR.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_StandardResiliencePipeline_SamplingDurationSatisfiesValidationInvariant()
    {
        using var app = BuildApp();

        var options = ResolveResilienceOptions(app);

        Assert.True(
            options.CircuitBreaker.SamplingDuration >= options.AttemptTimeout.Timeout * 2,
            $"CircuitBreaker.SamplingDuration ({options.CircuitBreaker.SamplingDuration}) " +
            $"must be >= 2 * AttemptTimeout ({options.AttemptTimeout.Timeout * 2}) " +
            "per HttpStandardResilienceOptions.Validate.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // TotalRequestTimeout must be > AttemptTimeout (sanity check).
    // If these are equal or inverted, every first attempt would exhaust
    // the total budget before any retry fires.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_StandardResiliencePipeline_TotalTimeoutExceedsAttemptTimeout()
    {
        using var app = BuildApp();

        var options = ResolveResilienceOptions(app);

        Assert.True(
            options.TotalRequestTimeout.Timeout > options.AttemptTimeout.Timeout,
            $"TotalRequestTimeout ({options.TotalRequestTimeout.Timeout}) must be > " +
            $"AttemptTimeout ({options.AttemptTimeout.Timeout}) so at least one retry is possible.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Smoke test: AddServiceDefaults builds without DI or options-validation
    // exceptions.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddServiceDefaults_HostBuild_Succeeds()
    {
        using var app = BuildApp();

        Assert.NotNull(app);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static HttpStandardResilienceOptions ResolveResilienceOptions(WebApplication app)
    {
        // The options name for the pipeline configured via ConfigureHttpClientDefaults
        // is "<clientName>-standard" where clientName="" for the global default client.
        // Verified by inspecting IHttpStandardResiliencePipelineBuilder.PipelineName
        // in a diagnostic test: ConfigureHttpClientDefaults yields pipeline name "-standard".
        var monitor = app.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>();

        return monitor.Get(PipelineOptionsName);
    }
}
