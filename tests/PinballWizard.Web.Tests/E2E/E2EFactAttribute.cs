using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// Skips E2E tests unless the live-stack env vars are present — they
// drive the real dev Azure stack (Cosmos / AI Search / Foundry) and
// each ask flow costs a real model call. CI additionally excludes
// Category=E2E by filter; this attribute makes a bare local
// `dotnet test` skip cleanly instead of failing on missing config.
//
// Run locally via tools/e2e/Run-E2E.ps1, which sets the env and runs
// the filtered suite.
public sealed class E2EFactAttribute : FactAttribute
{
    private static readonly string[] RequiredEnvVars =
    [
        "Cosmos__AccountEndpoint",
        "AiSearch__Endpoint",
        "AiFoundry__ProjectEndpoint",
    ];

    public static bool IsConfigured =>
        RequiredEnvVars.All(name =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

    public E2EFactAttribute()
    {
        if (!IsConfigured)
        {
            Skip = "E2E live-stack env not configured — set " +
                   string.Join(", ", RequiredEnvVars) +
                   " (see tools/e2e/Run-E2E.ps1).";
        }
    }
}
