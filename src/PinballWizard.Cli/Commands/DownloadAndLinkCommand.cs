using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Linking;

namespace PinballWizard.Cli.Commands;

// Combined download-then-link verb (Task 5a).
// Runs DocumentDownloadService.RunAsync (incremental blob download) first,
// then IDocumentLinker (link from raw → reads page-1 from blob), so the
// nightly ACA job has page-1 content available for edition resolution at
// link time. Missing-service sets exit code 2 and skips the link stage —
// IDocumentLinker resolves from the same Cosmos wiring, so it would fail
// identically. A per-document download failure sets exit code 1 but does
// NOT skip linking: the linker degrades gracefully when a file is absent
// (2026-07-03 reload: 74 expected per-doc download failures skipped the
// entire link stage, leaving the corpus unlinked until a manual
// --link-documents recovery run). Exit code 1 still propagates from
// whichever stage last reports a failure.
internal static class DownloadAndLinkCommand
{
    internal static async Task RunAsync(
        IServiceProvider services, CancellationToken cancellationToken, bool force = false)
    {
        await DownloadDocumentsCommand.RunAsync(services, cancellationToken, force);

        // Only a missing service (exit code 2) makes the link stage skip —
        // it would resolve the same missing Cosmos wiring and fail the same way.
        if (Environment.ExitCode == 2)
            return;

        await LinkDocumentsCommand.RunAsync(services, cancellationToken, relinkAll: false);
    }
}
