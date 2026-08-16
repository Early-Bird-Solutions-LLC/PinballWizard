using Xunit;

namespace PinballWizard.ServiceDefaults.Tests.ServiceDefaults;

// Serializes every test class that starts a host with OpenTelemetry configured.
//
// WHY THIS EXISTS: ActivityListener registration is PROCESS-GLOBAL. Starting a host
// builds a TracerProvider, which subscribes a listener for the whole process — not
// just for that host. So OpenTelemetryHostLifecycleTests'
// TracerProvider_ReturnsNullActivity_WhenHostIsNotStarted, whose entire premise is
// "no listener is subscribed, therefore StartActivity returns null", is only valid
// while NO other test anywhere in the process has a started host.
//
// xUnit runs distinct test classes in parallel by default (each class is its own
// collection). OpenTelemetryExporterTests and OpenTelemetryResourceTests both start
// hosts, so either could overlap that assertion and flip it from null to non-null —
// a genuine race, not a broken assertion. It surfaced as an intermittent CI failure:
//   Assert.Null() Failure: Value is not null
// and does not reproduce reliably on a developer machine, because whether the windows
// overlap depends on core count and timing.
//
// Sharing one collection makes these classes run sequentially, which removes the race
// by construction rather than by hoping the timing stays favourable. Cheap: the whole
// assembly is 27 tests in about a second.
//
// A test class in this assembly that starts a host MUST join this collection.
[CollectionDefinition(Name)]
public sealed class OpenTelemetryGlobalStateDefinition
{
    public const string Name = "OpenTelemetry global state";
}
