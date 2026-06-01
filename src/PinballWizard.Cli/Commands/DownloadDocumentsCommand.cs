using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Downloading;

namespace PinballWizard.Cli.Commands;

internal static class DownloadDocumentsCommand
{
    internal static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
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

        Console.WriteLine("Downloading not-yet-downloaded documents (polite, idempotent)...");
        var summary = await svc.RunAsync(cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"--download-documents complete: " +
            $"downloaded={summary.Downloaded} skipped={summary.Skipped} failed={summary.Failed}");

        if (summary.Failed > 0)
            Environment.ExitCode = 1;
    }
}
