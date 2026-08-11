using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Downloading;

namespace PinballWizard.Cli.Commands;

internal static class DownloadDocumentsCommand
{
    internal static async Task RunAsync(
        IServiceProvider services, CancellationToken cancellationToken, bool force = false)
    {
        var svc = services.GetService<DocumentDownloadService>();
        if (svc is null)
        {
            Console.Error.WriteLine(
                "--download-documents requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine(force
            ? "Re-downloading ALL documents (force, unconditional GET, polite)..."
            : "Downloading not-yet-downloaded documents (polite, idempotent)...");
        var summary = await svc.RunAsync(force, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"--download-documents complete: " +
            $"downloaded={summary.Downloaded} skipped={summary.Skipped} failed={summary.Failed} " +
            $"skipped_too_large={summary.SkippedTooLarge} backfilled={summary.Backfilled}");

        // Only UNEXPECTED failures drive the non-zero exit code. TooLarge docs are
        // permanent terminal skips under the current cap — they are expected, visible,
        // and metered (pinwiz.download.too_large_skip_total) but not operationally
        // actionable until MaxFileSizeBytes is deliberately raised.
        if (summary.Failed > 0)
            Environment.ExitCode = 1;
    }
}
