using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Provenance;

namespace PinballWizard.Cli.Commands;

internal static class MigrateToRawCommand
{
    internal static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var rawRepo = services.GetService<IRawDocumentRepository>();
        if (rawRepo is null)
        {
            Console.Error.WriteLine(
                "--migrate-to-raw requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        var catalogBuilder = services.GetRequiredService<CatalogBuilder>();
        var catalog = await catalogBuilder.LoadCatalogAsync(cancellationToken);

        Console.WriteLine($"Migrating {catalog.Documents.Count} documents from catalog.json to scraped_documents_raw...");

        var upserted = 0;
        var failed = 0;

        foreach (var doc in catalog.Documents)
        {
            try
            {
                await rawRepo.UpsertRawAsync(doc, cancellationToken);
                upserted++;

                if (upserted % 100 == 0)
                    Console.WriteLine($"  Progress: {upserted}/{catalog.Documents.Count} upserted...");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  Failed to upsert {doc.DocumentId}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"--migrate-to-raw complete: {upserted} upserted, {failed} failed.");

        if (failed > 0)
            Environment.ExitCode = 1;
    }
}
