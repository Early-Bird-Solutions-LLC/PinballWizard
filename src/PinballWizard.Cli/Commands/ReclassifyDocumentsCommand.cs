using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Documents;

namespace PinballWizard.Cli.Commands;

// CLI handler for --reclassify-documents.
//
// Resolves IDocumentReclassifier from DI (only registered when Cosmos
// is configured) and runs the in-place reclassification pass. Prints
// a summary to stdout and exits non-zero if any per-document failures
// occurred. Mirrors the exit-code-2 remediation pattern of sibling verbs.
internal static class ReclassifyDocumentsCommand
{
    internal static async Task RunAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var reclassifier = services.GetService<IDocumentReclassifier>();
        if (reclassifier is null)
        {
            Console.Error.WriteLine(
                "--reclassify-documents requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("Reclassifying documents — streaming scraped_documents_raw and re-running classification...");
        var result = await reclassifier.RunAsync(cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"--reclassify-documents complete: " +
            $"scanned={result.Scanned} reclassified={result.Reclassified} " +
            $"unchanged={result.Unchanged} failed={result.Failed}");

        if (result.Reclassified > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Transitions:");
            foreach (var t in result.Transitions)
            {
                Console.WriteLine($"  {t.OldType} → {t.NewType}  {t.DocumentId}  {t.DocumentUrl}");
            }

            Console.WriteLine();
            Console.WriteLine(
                "Next steps to activate the updated types in the RAG index:");
            Console.WriteLine(
                "  1. --relink-all   (fans updated document_type into scraped_documents; triggers change feed)");
            Console.WriteLine(
                "  2. Wait for the RagIngestionWorker to pick up the change-feed writes (or run --run-rag-backfill).");
        }

        if (result.Failed > 0)
            Environment.ExitCode = 1;
    }
}
