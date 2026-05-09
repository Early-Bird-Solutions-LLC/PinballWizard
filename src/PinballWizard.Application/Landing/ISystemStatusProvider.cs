namespace PinballWizard.Application.Landing;

// Abstracts the three Azure dependency probes (Foundry + AI Search + Cosmos)
// behind a single, cache-friendly interface so LandingService can populate
// SystemStatus without depending on any Infrastructure types directly.
//
// The implementation (Infrastructure/Landing/SystemStatusProvider) runs all
// three probes in parallel via Task.WhenAll and caches the result for
// SystemStatusOptions.CacheTtl (default 30 seconds) to avoid hammering
// Azure resources on every landing-page request.
//
// Registered as a singleton in Infrastructure's DI extension. The interface
// lives in Application so LandingService (Application) can depend on it
// without violating Clean Architecture's dependency direction rule.
public interface ISystemStatusProvider
{
    /// <summary>
    /// Returns the current <see cref="SystemStatus"/> for the three Azure
    /// dependencies. Results are cached; concurrent callers within the TTL
    /// window share a single probe result. <c>null</c> fields indicate that
    /// the probe result was genuinely unknown (e.g., the dependency is not
    /// configured in the current environment).
    /// </summary>
    Task<SystemStatus> GetStatusAsync(CancellationToken cancellationToken);
}
