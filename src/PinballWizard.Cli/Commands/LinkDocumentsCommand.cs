using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Linking;

namespace PinballWizard.Cli.Commands;

internal static class LinkDocumentsCommand
{
    internal static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
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

        await linker.InitializeAsync(cancellationToken);

        Console.WriteLine("Linking documents — scanning for pending, failed, and not_in_catalog records...");
        var (processed, linked, platformGeneric, notInCatalog, failed) =
            await linker.RunBatchAsync(cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"--link-documents complete: " +
            $"processed={processed} linked={linked} platform_generic={platformGeneric} " +
            $"not_in_catalog={notInCatalog} failed={failed}");

        if (failed > 0)
            Environment.ExitCode = 1;
    }
}
