using Azure.Core;
using Azure.Identity;

namespace PinballWizard.Infrastructure.Credentials;

// ONE process-wide TokenCredential (issue #362).
//
// The 2026-06-11 eval outage mechanism: nine call sites each constructed
// their own DefaultAzureCredential, each with its own token cache, each
// shelling out to `az` independently — under parallel eval load the
// concurrent az.cmd spawns exceeded the CLI credential's default process
// timeout and token acquisition died mid-run. One shared instance means
// one token cache absorbing refreshes; ProcessTimeout covers the residual
// slow-spawn case on a busy dev machine.
//
// Deployed hosts are unaffected either way (DefaultAzureCredential resolves
// ManagedIdentityCredential there; ProcessTimeout only governs the CLI
// fallback chain) — which is why one shared static suffices for all hosts.
//
// Namespace note: NOT PinballWizard.Infrastructure.Azure — that final
// segment shadows the Azure root namespace for every file under
// PinballWizard.Infrastructure.*, breaking Azure.RequestFailedException
// style references (found the hard way).
public static class SharedAzureCredential
{
    private static readonly Lazy<TokenCredential> LazyInstance = new(
        () => new DefaultAzureCredential(BuildOptions(IsDevelopment)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static TokenCredential Instance => LazyInstance.Value;

    // True when the host runs in the Development environment (local dev). Read
    // from the env var rather than IHostEnvironment so this stays a dependency-
    // free static usable from every host's DI wiring.
    private static bool IsDevelopment =>
        string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

    // Internal + parameterized so the dev-vs-deployed decision is unit-testable.
    internal static DefaultAzureCredentialOptions BuildOptions(bool isDevelopment)
    {
        var options = new DefaultAzureCredentialOptions
        {
            CredentialProcessTimeout = TimeSpan.FromSeconds(30),
        };

        // A developer machine has no IMDS endpoint (169.254.169.254), so
        // ManagedIdentityCredential's probe can never succeed locally — it just
        // burns a multi-second network timeout and then emits the loudest line in
        // the aggregate failure ("All Managed Identity sources are unavailable"),
        // which masks the real local cause (not `az login`'d, or signed into the
        // wrong tenant). Excluding it — and the k8s-only workload identity
        // credential — in Development sends the chain straight to the Azure CLI /
        // Visual Studio developer credentials: faster, and with a clear error when
        // there is genuinely no signed-in session. Deployed hosts
        // (ASPNETCORE_ENVIRONMENT != Development) keep both: Managed Identity is
        // the ONLY credential available there.
        if (isDevelopment)
        {
            options.ExcludeManagedIdentityCredential = true;
            options.ExcludeWorkloadIdentityCredential = true;
        }

        return options;
    }
}
