namespace PinballWizard.Application.Landing;

// Configuration for the SystemStatusProvider cache.
// Bound from appsettings.json section "Landing:SystemStatus".
//
// CacheTtl controls how long a probe result is reused before the provider
// re-runs the three Azure dependency checks. Default is 30 seconds —
// cheap enough that a degradation shows up in the next landing-page
// request cycle, but long enough that a steady stream of anonymous
// visitors does not trigger a probe per request.
//
// Kept in Application (not Infrastructure) so that the default can be
// unit-tested without spinning up the full Infrastructure stack.
public sealed record SystemStatusOptions
{
    /// <summary>Configuration section name bound by DI.</summary>
    public const string SectionName = "Landing:SystemStatus";

    /// <summary>
    /// How long a probe result is cached before the provider re-probes.
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(30);
}
