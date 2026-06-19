using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Documents;
using PinballWizard.Infrastructure.Credentials;

namespace PinballWizard.Infrastructure.Documents;

public static class BlobDocumentStoreRegistration
{
    // Config key used by the Aspire host to inject the Azurite connection string
    // for local dev (matches storage.AddBlobs("blobs") in AppHost/Program.cs).
    private const string AspireBlobsConnectionName = "blobs";

    // Config key for the deployed storage blob service endpoint, mirroring the
    // Cosmos endpoint pattern (Cosmos:AccountEndpoint). Sourced from the Bicep
    // output 'storageBlobEndpoint' (shared.bicep line ~2084). Operators set
    // Storage__BlobEndpoint on the ACA container env (double-underscore = colon
    // in config). Example: https://<storageAccountName>.blob.core.windows.net/
    private const string DeployedBlobEndpointKey = "Storage:BlobEndpoint";

    public static IServiceCollection AddDocumentBlobStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var aspireConnection = configuration.GetConnectionString(AspireBlobsConnectionName);
        var deployedEndpoint = configuration[DeployedBlobEndpointKey];

        BlobServiceClient? serviceClient = null;

        if (!string.IsNullOrWhiteSpace(aspireConnection))
        {
            // Local dev: Aspire injects the Azurite connection string via
            // ConnectionStrings:blobs (storage.AddBlobs("blobs") in AppHost).
            serviceClient = new BlobServiceClient(aspireConnection);
        }
        else if (!string.IsNullOrWhiteSpace(deployedEndpoint))
        {
            // Deployed: use managed identity via SharedAzureCredential.
            // The endpoint is sourced from the Bicep output storageBlobEndpoint
            // and set as Storage__BlobEndpoint on the ACA container.
            serviceClient = new BlobServiceClient(
                new Uri(deployedEndpoint),
                SharedAzureCredential.Instance);
        }

        if (serviceClient is null)
        {
            // Neither signal present — no blob storage configured. Callers
            // that need IDocumentBlobStore will get a DI resolution error,
            // which is the correct loud failure (no silent degradation).
            return services;
        }

        var containerClient = serviceClient.GetBlobContainerClient(BlobDocumentStore.ContainerName);

        services.AddSingleton<IDocumentBlobStore>(sp =>
            new BlobDocumentStore(
                containerClient,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BlobDocumentStore>>()));

        return services;
    }
}
