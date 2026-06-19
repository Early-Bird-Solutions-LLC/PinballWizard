using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Linking;

namespace PinballWizard.Cli.Commands;

// Combined download-then-link verb (Task 5a).
// Runs DocumentDownloadService.RunAsync (incremental blob download) first,
// then IDocumentLinker (link from raw → reads page-1 from blob), so the
// nightly ACA job has page-1 content available for edition resolution at
// link time. Non-zero exit if either stage fails: download failure sets
// exit code 1, link failure sets exit code 1, missing-service sets exit
// code 2. If download is missing (Cosmos not wired), we stop before
// attempting the link stage.
internal static class DownloadAndLinkCommand
{
    internal static async Task RunAsync(
        IServiceProvider services, CancellationToken cancellationToken, bool force = false)
    {
        await DownloadDocumentsCommand.RunAsync(services, cancellationToken, force);

        // If the download stage failed due to missing service (exit code 2)
        // or a download error (exit code 1), propagate and skip the link stage.
        if (Environment.ExitCode != 0)
            return;

        await LinkDocumentsCommand.RunAsync(services, cancellationToken, relinkAll: false);
    }
}
