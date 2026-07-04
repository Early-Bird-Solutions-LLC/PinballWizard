using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;

namespace PinballWizard.Cli.Commands;

// CLI handler for --backfill-manufacturer-slugs (issue #672).
//
// Resolves IScraperReconciliationService + IRawDocumentRepository from DI
// (both registered whenever Cosmos is configured) and runs the
// cross-reference slug backfill over the full scraped_documents_raw stream.
// Mirrors the exit-code-2 remediation pattern of sibling verbs.
internal static class BackfillManufacturerSlugsCommand
{
    internal static async Task RunAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var reconciler = services.GetService<IScraperReconciliationService>();
        var rawRepo = services.GetService<IRawDocumentRepository>();
        if (reconciler is null || rawRepo is null)
        {
            Console.Error.WriteLine(
                "--backfill-manufacturer-slugs requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine(
            "Backfilling ManufacturerSlugs from cross-reference provenance already in scraped_documents_raw...");

        var result = await reconciler.BackfillSlugsFromCrossReferencesAsync(
            rawRepo.StreamAllAsync(cancellationToken), cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"--backfill-manufacturer-slugs complete: " +
            $"candidates={result.CandidatesConsidered} alreadyPresent={result.AlreadyPresent} " +
            $"matchedSingle={result.MatchedSingle} matchedGroup={result.MatchedGroup} " +
            $"unmatched={result.Unmatched} ambiguous={result.Ambiguous} upserts={result.Upserts}");

        if (result.Upserts > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Next step: run --relink-all so the linker's Tier 1 (xref_slug) re-resolves");
            Console.WriteLine("documents against the newly-backfilled slugs.");
        }

        // Ambiguous slugs need manual triage (see the WARNING log lines above);
        // a non-zero exit lets a cron/CI run alert rather than silently no-op.
        if (result.Ambiguous > 0)
            Environment.ExitCode = 3;
    }
}
