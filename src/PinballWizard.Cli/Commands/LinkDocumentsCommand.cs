using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Cli.Commands;

internal static class LinkDocumentsCommand
{
    // Maximum number of RunBatchAsync iterations in --relink-all mode.
    //
    // Why this bound is unreachable in practice: xref_slug_resolver targets
    // must be Linked before the documents that reference them can resolve.
    // Each pass converts at least one target to Linked (otherwise linked == 0
    // and the loop exits). The corpus dependency graph is a DAG over a finite
    // not_in_catalog set, so each pass strictly reduces what remains. Measured
    // live on 2026-08-11: 475 linked in pass 1, +156 in pass 2, 0 in pass 3
    // (two passes to steady state). A chain of depth N needs N passes; today's
    // deepest xref chain is 1 hop. Ten passes implies a 10-hop dependency chain
    // — absent from the current corpus and not expected from any manufacturer's
    // document structure.
    private const int RelinkMaxPasses = 10;

    internal static async Task RunAsync(
        IServiceProvider services,
        CancellationToken cancellationToken,
        bool relinkAll = false)
    {
        var linker = services.GetService<IDocumentLinker>();
        if (linker is null)
        {
            Console.Error.WriteLine(
                "--link-documents requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        // Tier 3/4 of the linking algorithm (page-text matching) requires IDocumentTextExtractor.
        // With the current DI wiring it is registered whenever cosmosWired, but guard here so
        // a future misconfiguration is loud rather than silent (OBS-01 / issue #654).
        if (services.GetService<IDocumentTextExtractor>() is null)
        {
            Console.Error.WriteLine(
                "WARNING: IDocumentTextExtractor is not registered — Tiers 3/4 (page-text " +
                "matching) will be skipped for this run. If Cosmos is configured, ensure " +
                "AddPdfDocumentTextExtractor is called in the DI composition root.");
        }

        // InitializeAsync builds the resolver catalog from Cosmos. Called once regardless
        // of pass count — the catalog is stable for the lifetime of a single run.
        await linker.InitializeAsync(cancellationToken);

        if (relinkAll)
        {
            // --relink-all: reset previously-Linked/NotInCatalog docs to Pending so
            // the (fixed) tiers re-run against them. Reset is performed ONCE before the
            // iteration loop; repeating it between passes would undo inter-pass progress.
            // Admin overrides (ManuallyLinked) and PlatformGeneric are intentionally preserved.
            Console.WriteLine("Re-link mode — resetting Linked/NotInCatalog documents to Pending...");
            var reset = await linker.ResetForRelinkAsync(cancellationToken);
            Console.WriteLine($"Reset {reset} document(s) to Pending.");

            await RunRelinkPassesAsync(linker, cancellationToken);
        }
        else
        {
            // Plain --link-documents: single pass only. The nightly job converges the
            // corpus incrementally across successive nightly runs (new scraper output
            // lands a batch at a time), so multi-pass iteration here is unnecessary and
            // would add Cosmos RU load proportional to the not_in_catalog residual.
            await RunSinglePassAsync(linker, cancellationToken);
        }
    }

    // Iterates RunBatchAsync until convergence (no new links in a pass) or the
    // hard pass limit is reached. See RelinkMaxPasses for why the limit is safe.
    private static async Task RunRelinkPassesAsync(
        IDocumentLinker linker,
        CancellationToken cancellationToken)
    {
        int totalProcessed = 0, totalLinked = 0, totalPlatformGeneric = 0,
            totalFailed = 0, totalNeedsReview = 0;
        // not_in_catalog is a snapshot of the remaining unresolved count after each
        // pass — we keep only the last value for the aggregate summary.
        int lastNotInCatalog = 0;
        int pass = 0;

        int passLinked;
        do
        {
            pass++;
            Console.WriteLine($"Pass {pass} — scanning for pending, failed, and not_in_catalog records...");

            var (processed, linked, platformGeneric, notInCatalog, failed, needsReview) =
                await linker.RunBatchAsync(cancellationToken);

            passLinked = linked;
            totalProcessed += processed;
            totalLinked += linked;
            totalPlatformGeneric += platformGeneric;
            lastNotInCatalog = notInCatalog;
            totalFailed += failed;
            totalNeedsReview += needsReview;

            Console.WriteLine(
                $"  Pass {pass} result: processed={processed} linked={linked} " +
                $"platform_generic={platformGeneric} not_in_catalog={notInCatalog} " +
                $"failed={failed} needs_review={needsReview}");

        } while (passLinked > 0 && pass < RelinkMaxPasses);

        if (passLinked > 0 && pass >= RelinkMaxPasses)
        {
            Console.Error.WriteLine(
                $"WARNING: --relink-all reached the {RelinkMaxPasses}-pass hard limit with " +
                $"{passLinked} new link(s) in the last pass. The corpus may not have converged — " +
                "inspect not_in_catalog records for unexpected dependency cycles.");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"--relink-all complete ({pass} pass{(pass == 1 ? "" : "es")}): " +
            $"processed={totalProcessed} linked={totalLinked} " +
            $"platform_generic={totalPlatformGeneric} not_in_catalog={lastNotInCatalog} " +
            $"failed={totalFailed} needs_review={totalNeedsReview}");

        if (totalFailed > 0)
            Environment.ExitCode = 1;
    }

    private static async Task RunSinglePassAsync(
        IDocumentLinker linker,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Linking documents — scanning for pending, failed, and not_in_catalog records...");
        var (processed, linked, platformGeneric, notInCatalog, failed, needsReview) =
            await linker.RunBatchAsync(cancellationToken);

        Console.WriteLine();
        Console.WriteLine(
            $"--link-documents complete: " +
            $"processed={processed} linked={linked} platform_generic={platformGeneric} " +
            $"not_in_catalog={notInCatalog} failed={failed} needs_review={needsReview}");

        if (failed > 0)
            Environment.ExitCode = 1;
    }
}
