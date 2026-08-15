using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace PinballWizard.ServiceDefaults.Tests.ServiceDefaults;

/// <summary>
/// Behavioral tests that guard the CLI's host-lifecycle fix for GitHub #840.
///
/// Root cause (proven by local reproduction before this fix):
///   <c>AddOpenTelemetry()</c> in ServiceDefaults registers <c>TracerProvider</c> and
///   <c>MeterProvider</c> as lazy DI singletons.  These singletons are created by
///   <c>TelemetryHostedService.StartAsync()</c> (from OpenTelemetry.Extensions.Hosting).
///   Without that call the providers are never instantiated, so no
///   <see cref="ActivityListener"/> is registered for the PinballWizard
///   <see cref="ActivitySource"/> — and <c>ActivitySource.StartActivity()</c> returns
///   <see langword="null"/> for every span, making all telemetry a silent no-op even when
///   <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is present.
///
/// The CLI previously built the host but never called <c>StartAsync()</c>. The fix
/// (Program.cs) calls <c>await host.StartAsync()</c> before any command handler runs and
/// wraps the handler body in a <c>try/finally</c> so <c>await host.StopAsync()</c> always
/// fires — triggering <c>ForceFlush()</c> so buffered telemetry reaches Azure Monitor
/// before the process exits.
///
/// These tests guard BOTH sides of that invariant:
/// <list type="number">
///   <item>After <c>StartAsync</c>: the ActivitySource is subscribed and activities are recorded.</item>
///   <item>Without <c>StartAsync</c>: the ActivitySource has no listener and
///         <c>StartActivity</c> returns <see langword="null"/> — the pre-fix failure mode,
///         documented here so a future OTel SDK change that alters this lazy-init contract
///         would fail loudly and tell the next person why.</item>
/// </list>
/// </summary>
/// <remarks>
/// ActivitySource name "PinballWizard" matches the constant
/// <c>PinballWizardTelemetry.ActivitySourceName</c> in
/// <c>src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs</c>.
/// It is also the name registered in ServiceDefaults via the local constant
/// <c>PinballWizardSourceName = "PinballWizard"</c> in Extensions.cs.
/// Both must stay in sync; this string is the load-bearing bridge.
/// </remarks>
[Collection(OpenTelemetryGlobalStateDefinition.Name)]
public sealed class OpenTelemetryHostLifecycleTests
{
    // The exact ActivitySource name declared in PinballWizardTelemetry.ActivitySourceName
    // and registered in ServiceDefaults/Extensions.cs (ConfigureOpenTelemetry).
    // These two locations MUST stay in sync; this constant is the test-layer assertion of that contract.
    private const string PinballWizardActivitySourceName = "PinballWizard";

    // ─────────────────────────────────────────────────────────────────────
    // Happy path: StartAsync → ActivityListener registered → activity recorded
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TracerProvider_IsSubscribedToActivitySource_WhenHostIsStarted()
    {
        // Arrange: build a generic host that mirrors the CLI's CreateHost path
        // (IHostApplicationBuilder → AddServiceDefaults → Build).
        // No App Insights connection string needed — the TracerProvider registers
        // an ActivityListener regardless of exporters. Exporters only affect where
        // spans are *sent*, not whether they are *sampled* by StartActivity().
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        using var host = builder.Build();

        // Act: start the host — TelemetryHostedService.StartAsync() fires, creating
        // TracerProvider and registering its ActivityListener on the global source.
        await host.StartAsync();
        try
        {
            // Assert: an activity started on the PinballWizard source must be non-null.
            // A null return here means no listener is subscribed — the #840 failure mode.
            // ActivitySource is IDisposable and registers itself globally on
            // construction — hold it in its own `using` so it is deregistered
            // deterministically rather than lingering for the test-host lifetime.
            // Declared before `activity` so disposal runs activity-then-source.
            using var source = new ActivitySource(PinballWizardActivitySourceName);
            using var activity = source.StartActivity("test.span");

            Assert.NotNull(activity);
        }
        finally
        {
            // StopAsync triggers ForceFlush on providers and stops IHostedServices cleanly.
            await host.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Failure-mode documentation: WITHOUT StartAsync → null activity (pre-fix behavior)
    //
    // This test is intentionally documenting the broken state the CLI had before #840.
    // If it begins failing (StartActivity returns non-null without StartAsync), it means
    // the OTel SDK has changed its lazy-init contract — the premise of the #840 fix
    // no longer holds and the host-lifecycle code in Program.cs should be re-evaluated.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TracerProvider_ReturnsNullActivity_WhenHostIsNotStarted()
    {
        // Arrange: same DI setup but deliberately skip StartAsync.
        // The TracerProvider singleton is never created, so no ActivityListener fires.
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        using var host = builder.Build();

        // Act: attempt to start an activity WITHOUT calling host.StartAsync().
        // Pre-fix: this is exactly what the CLI did — built the host, resolved
        // services directly, and ran the command, all with providers inert.
        // Same disposal discipline as the started-host test above: the source owns a
        // global registration, so it gets its own `using` declared before `activity`.
        using var source = new ActivitySource(PinballWizardActivitySourceName);
        using var activity = source.StartActivity("test.span.no.start");

        // Assert: null — the SDK returns null when no listener is subscribed.
        // If this assertion ever fails, the OTel SDK no longer uses lazy-init
        // for the ActivityListener; revisit the StartAsync requirement in Program.cs.
        Assert.Null(activity);

        // Dispose cleanly without ever calling StartAsync/StopAsync.
        await Task.CompletedTask;
    }
}
