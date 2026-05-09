namespace PinballWizard.Application.Landing;

// Thin abstraction for the Cosmos connectivity canary used by
// ISystemStatusProvider. Returns true on success, false on a Cosmos-level
// error, and null when Cosmos is not configured in the current environment
// (the implementation is an optional DI dependency in SystemStatusProvider).
//
// Using a dedicated interface rather than IHealthCheck keeps the probe
// independent of the ASP.NET Core health-check middleware pipeline and
// makes the contract testable without the HealthCheckContext ceremony.
// The Infrastructure implementation (CosmosCanaryProbe) mirrors the
// CosmosHealthCheck ReadContainerAsync approach against the "machines"
// canary container per ADR-0025 § 8.
public interface ICosmosCanaryProbe
{
    /// <summary>
    /// Probes Cosmos connectivity using a lightweight
    /// <c>ReadContainerAsync</c> call against the <c>machines</c>
    /// canary container. Returns <c>true</c> on success,
    /// <c>false</c> on a <see cref="Microsoft.Azure.Cosmos.CosmosException"/>,
    /// and lets non-Cosmos exceptions propagate (the caller maps
    /// those to <c>null</c>).
    /// </summary>
    Task<bool> ProbeAsync(CancellationToken cancellationToken);
}
