using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Downloading;

namespace PinballWizard.Cli.Commands;

internal static class MigrateDownloadPathsCommand
{
    internal static async Task RunAsync(IServiceProvider services, bool dryRun, CancellationToken cancellationToken)
    {
        // DEPRECATED (ADR-0039). This command normalises legacy *on-disk* download
        // paths, but the downloader no longer writes to disk — documents are
        // streamed straight into the pinwiz-raw blob container (File.LocalPath is
        // now a blob key, not a disk path). There is no disk layout left to migrate,
        // so this command is a no-op against any post-ADR-0039 corpus and is slated
        // for removal in a follow-up PR. Emitted before the Cosmos check so the
        // notice is visible even when run without a configured account.
        Console.Error.WriteLine(
            "WARNING: --migrate-download-paths is DEPRECATED (ADR-0039). The downloader writes " +
            "documents to the pinwiz-raw blob container, not local disk, so there are no on-disk " +
            "paths left to migrate. This command will be removed in a future release. No action needed.");

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
            $"migrated={summary.Migrated} (unverified={summary.MigratedUnverified}) skipped={summary.Skipped} " +
            $"shaMismatch={summary.ShaMismatch} missing={summary.Missing}");

        // A SHA mismatch or a missing file is an integrity problem the operator
        // must see — surface it as a non-zero exit so it can't pass silently.
        if (summary.ShaMismatch > 0 || summary.Missing > 0)
            Environment.ExitCode = 1;
    }
}
