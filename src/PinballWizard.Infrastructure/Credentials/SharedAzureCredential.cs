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
        () => new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            CredentialProcessTimeout = TimeSpan.FromSeconds(30),
        }),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static TokenCredential Instance => LazyInstance.Value;
}
