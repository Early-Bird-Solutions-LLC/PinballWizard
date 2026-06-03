using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Downloading;

namespace PinballWizard.Cli.Commands;

internal static class MigrateDownloadPathsCommand
{
    internal static async Task RunAsync(IServiceProvider services, bool dryRun, CancellationToken cancellationToken)
    {
        var svc = services.GetService<DownloadPathMigrationService>();
        if (svc is null)
        {
            Console.Error.WriteLine(
                "--migrate-download-paths requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine(dryRun
            ? "Migrating download paths (DRY-RUN — no files moved, no Cosmos writes)..."
            : "Migrating download paths (verify SHA → move file → rewrite local_path)...");

        var summary = await svc.RunAsync(dryRun, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"--migrate-download-paths {(dryRun ? "(dry-run) " : "")}complete: " +
            $"migrated={summary.Migrated} skipped={summary.Skipped} " +
            $"shaMismatch={summary.ShaMismatch} missing={summary.Missing}");

        // A SHA mismatch or a missing file is an integrity problem the operator
        // must see — surface it as a non-zero exit so it can't pass silently.
        if (summary.ShaMismatch > 0 || summary.Missing > 0)
            Environment.ExitCode = 1;
    }
}
