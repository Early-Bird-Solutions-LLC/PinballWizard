using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// Skips E2E tests unless one of the two run modes is configured —
// they drive a real stack and each ask flow costs a real model call:
//
//   - E2E__BaseUrl: deployed-target mode (post-deploy canary in
//     deploy.yml — points at the wizard app's ACA FQDN).
//   - Cosmos/AiSearch/AiFoundry env vars: local spawn mode
//     (tools/e2e/Run-E2E.ps1).
//
// PR CI additionally excludes Category=E2E by filter; this attribute
// makes a bare local `dotnet test` skip cleanly instead of failing on
// missing config.
public sealed class E2EFactAttribute : FactAttribute
{
    private static readonly string[] LiveStackEnvVars =
    [
        "Cosmos__AccountEndpoint",
        "AiSearch__Endpoint",
        "AiFoundry__ProjectEndpoint",
    ];

    // Non-null when targeting an already-running deployment.
    public static string? DeployedBaseUrl
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("E2E__BaseUrl");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public static bool IsConfigured =>
        DeployedBaseUrl is not null
        || LiveStackEnvVars.All(name =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

    public E2EFactAttribute()
    {
        if (!IsConfigured)
        {
            Skip = "E2E not configured — set E2E__BaseUrl (deployed target) or " +
                   string.Join(", ", LiveStackEnvVars) +
                   " (local spawn; see tools/e2e/Run-E2E.ps1).";
        }
    }
}

// Theory counterpart to E2EFactAttribute — same skip logic, for
// [InlineData]-driven E2E cases (e.g. a route table checked one page per row).
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute()
    {
        if (!E2EFactAttribute.IsConfigured)
        {
            Skip = "E2E not configured — set E2E__BaseUrl (deployed target) or live-stack env vars " +
                   "(local spawn; see tools/e2e/Run-E2E.ps1).";
        }
    }
}
