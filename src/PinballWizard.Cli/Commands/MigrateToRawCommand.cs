using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Provenance;

namespace PinballWizard.Cli.Commands;

internal static class MigrateToRawCommand
{
    internal static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(MigrateToRawCommand).FullName!);

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

        using var semaphore = new SemaphoreSlim(initialCount: 8, maxCount: 8);

        var tasks = catalog.Documents.Select(async doc =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await rawRepo.UpsertRawAsync(doc, cancellationToken);
                var current = Interlocked.Increment(ref upserted);

                if (current % 100 == 0)
                    Console.WriteLine($"  Progress: {current}/{catalog.Documents.Count} upserted...");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to upsert {DocumentId} to scraped_documents_raw", doc.DocumentId);
                Interlocked.Increment(ref failed);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        Console.WriteLine();
        Console.WriteLine($"--migrate-to-raw complete: {upserted} upserted, {failed} failed.");

        if (failed > 0)
            Environment.ExitCode = 1;
    }
}
