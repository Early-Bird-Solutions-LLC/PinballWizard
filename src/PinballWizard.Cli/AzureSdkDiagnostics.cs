using System.Diagnostics.Tracing;
using Azure.Core.Diagnostics;

namespace PinballWizard.Cli;

// Opt-in Azure SDK event-source tracing, added to diagnose the #855 workspace
// connection failure (issue #920).
//
// Why this exists: Azure.Developer.Playwright's GetConnectOptionsAsync throws a bare
// `System.Exception: Could not authenticate with the service.` with NO inner exception
// and no status code. That single sentence is identical whether the caller holds no
// RBAC at all, holds the wrong role, or is refused for a non-identity reason — it was
// observed to be byte-identical for a deliberately garbage endpoint string during
// earlier investigation, which is precisely why it cannot be reasoned from. The
// underlying HTTP exchange (which Azure.Core does surface on its event source) is what
// actually distinguishes those cases.
//
// Enabling this listener emits Azure.Identity token-acquisition events and Azure.Core
// request/response events — including the response STATUS CODE from the Playwright data
// plane, which is the fact the investigation is missing.
//
// SAFETY: opt-in via PINWIZ_AZURE_SDK_DIAGNOSTICS=true, default OFF. Azure.Core's event
// source redacts header values that are not on its allow-list — `Authorization` is not
// on it, so bearer tokens are logged as REDACTED rather than in the clear. Even so this
// is deliberately not left on: it is verbose, it costs Log Analytics ingestion against
// the 1 GB cap, and a diagnostic switch nobody remembers turning on is how a temporary
// measure becomes permanent. Turn it off once #920 is resolved.
internal static class AzureSdkDiagnostics
{
    // Held in a static field on purpose. AzureEventSourceListener unsubscribes when it is
    // collected, so a local would silently stop producing events as soon as the GC ran —
    // a failure mode that looks exactly like "the SDK logged nothing", which would be
    // actively misleading in the middle of an investigation into missing detail.
    private static AzureEventSourceListener? _listener;

    internal static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("PINWIZ_AZURE_SDK_DIAGNOSTICS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    internal static void EnableIfConfigured()
    {
        if (!IsEnabled || _listener is not null)
        {
            return;
        }

        // Console, not ILogger: this must capture events raised during host construction
        // and during credential acquisition inside the SDK, both of which can happen
        // outside a scope where the logging pipeline is usable. ACA collects stdout into
        // ContainerAppConsoleLogs_CL either way, which is where the investigation reads.
        _listener = AzureEventSourceListener.CreateConsoleLogger(EventLevel.Verbose);
        Console.WriteLine(
            "[azure-sdk-diagnostics] Verbose Azure SDK tracing ENABLED via " +
            "PINWIZ_AZURE_SDK_DIAGNOSTICS. Expect high log volume; disable when done (#920).");
    }
}
