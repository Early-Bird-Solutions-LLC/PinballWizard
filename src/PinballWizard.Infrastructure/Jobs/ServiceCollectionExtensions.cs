using Azure.Core;
using Azure.ResourceManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Jobs;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Infrastructure.Jobs;

// DI registration for the ARM-backed jobs admin service.
public static class ServiceCollectionExtensions
{
    // Registers IJobAdminService.
    // Auth: uses the shared TokenCredential singleton (DefaultAzureCredential) already registered
    // by AddCosmosPersistence — same as ArmCosmosProvisioner.
    // Sub + RG are parsed from Cosmos:AccountResourceId to avoid requiring two additional
    // config values for information already present.
    // Returns false and registers nothing when Cosmos:AccountResourceId is absent (local dev
    // without live Azure) — Web page degrades visibly per Invariant #17.
    public static bool AddJobAdminService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // IJobAdminService is registered as a singleton factory using the
        // TokenCredential and CosmosOptions already in the container (the
        // latter is registered by AddCosmosPersistence, which runs first).
        // We parse sub/RG from Cosmos:AccountResourceId to avoid requiring
        // two additional config values for information already present.
        services.AddSingleton<IJobAdminService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ArmJobAdminService>>();
            var credential = sp.GetRequiredService<TokenCredential>();
            var armClient = new ArmClient(credential);

            // Derive subscription + RG from Cosmos:AccountResourceId.
            // Format: /subscriptions/{sub}/resourceGroups/{rg}/providers/...
            var cosmosOptions = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
            var (subscriptionId, resourceGroupName) = ParseSubAndRg(cosmosOptions.AccountResourceId);

            if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(resourceGroupName))
            {
                // Belt-and-suspenders: Program.cs only calls AddJobAdminService when
                // Cosmos:AccountResourceId is set, so this branch should never fire at runtime.
                throw new InvalidOperationException(
                    "IJobAdminService was registered but could not resolve SubscriptionId / ResourceGroup " +
                    "from Cosmos:AccountResourceId. Ensure AddJobAdminService is only called when " +
                    "Cosmos:AccountResourceId is set.");
            }

            return new ArmJobAdminService(armClient, subscriptionId, resourceGroupName, logger);
        });

        return true;
    }

    // Parse SubscriptionId and ResourceGroup from a Cosmos ARM resource ID.
    // Format: /subscriptions/{sub}/resourceGroups/{rg}/providers/...
    internal static (string? SubscriptionId, string? ResourceGroupName) ParseSubAndRg(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return (null, null);

        // Split by '/' and look for the "subscriptions" and "resourceGroups" segments.
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? sub = null;
        string? rg = null;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
                sub = parts[i + 1];
            else if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) ||
                     parts[i].Equals("resourcegroups", StringComparison.OrdinalIgnoreCase))
                rg = parts[i + 1];
        }

        return (sub, rg);
    }
}
