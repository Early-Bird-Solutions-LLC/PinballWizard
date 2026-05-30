using PinballWizard.Infrastructure.Integrations.AiSearch;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.AiSearch;

// Pin the URL-derivation rule used to construct the AzureOpenAIClient
// endpoint from the configured Foundry project endpoint. Foundry's
// project URLs have the shape
// `https://<account>.services.ai.azure.com/api/projects/<project>`
// and AzureOpenAIClient consumes the account-level URL — Foundry's
// unified endpoint design routes both project and OpenAI deployment
// calls through the same host. The DI extension's BuildRagRetriever
// path bypasses unit testing because it constructs Azure SDK clients
// against real endpoints; the derivation rule on its own is the unit
// that benefits from pinning here.
public sealed class AiSearchServiceCollectionExtensionsTests
{
    [Fact]
    public void DeriveAccountEndpoint_ProjectEndpoint_StripsPath()
    {
        var account = ServiceCollectionExtensions.DeriveAccountEndpoint(
            "https://pinwiz-foundry-dev.services.ai.azure.com/api/projects/myproject");

        Assert.Equal("https://pinwiz-foundry-dev.services.ai.azure.com/", account.ToString());
    }

    [Fact]
    public void DeriveAccountEndpoint_BareAccountEndpoint_PassesThrough()
    {
        var account = ServiceCollectionExtensions.DeriveAccountEndpoint(
            "https://pinwiz-foundry-dev.services.ai.azure.com/");

        Assert.Equal("https://pinwiz-foundry-dev.services.ai.azure.com/", account.ToString());
    }

    [Fact]
    public void DeriveAccountEndpoint_AlternateRegionHost_PreservesHost()
    {
        var account = ServiceCollectionExtensions.DeriveAccountEndpoint(
            "https://pinwiz-foundry-eastus.services.ai.azure.com/api/projects/x");

        Assert.Equal("https://pinwiz-foundry-eastus.services.ai.azure.com/", account.ToString());
    }
}
