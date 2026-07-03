using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Extraction;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Extraction;

// Unit tests for ServiceCollectionExtensions.AddPdfDocumentTextExtractor.
//
// These tests verify that the extension method correctly registers
// IDocumentTextExtractor into a plain ServiceCollection without requiring
// AI Search, Foundry, or any other external backend — isolating the
// registration behaviour from Program.cs's DI gate.
//
// Relevant to GitHub issue #654: the registration gate in Program.cs was
// previously cosmosWired && aiSearchWired && foundryWired, which meant the
// extractor was absent when only Cosmos was configured. The tests here confirm
// the method itself has no such restriction (PdfPigDocumentTextExtractor is a
// pure local library) so moving the call to the cosmosWired-only gate is safe.
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPdfDocumentTextExtractor_WithNoConfig_RegistersIDocumentTextExtractor()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(); // ILogger<T> infrastructure required by PdfPigDocumentTextExtractor

        // Act — no configuration at all: no ADI endpoint, no AI Search, no Foundry
        services.AddPdfDocumentTextExtractor(configuration: null);

        // Assert
        using var provider = services.BuildServiceProvider();
        var extractor = provider.GetService<IDocumentTextExtractor>();
        Assert.NotNull(extractor);
    }

    [Fact]
    public void AddPdfDocumentTextExtractor_WithNoAdiEndpoint_ResolvesPdfPigExtractor()
    {
        // Without DocumentIntelligence:Endpoint, the method registers the pure-local
        // PdfPigDocumentTextExtractor as the IDocumentTextExtractor singleton.
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Cosmos endpoint present — no ADI, no AI Search, no Foundry
                ["Cosmos:AccountEndpoint"] = "https://fake.documents.azure.com:443/"
            })
            .Build();

        services.AddPdfDocumentTextExtractor(config);

        using var provider = services.BuildServiceProvider();
        var extractor = provider.GetService<IDocumentTextExtractor>();

        Assert.NotNull(extractor);
        Assert.IsType<PdfPigDocumentTextExtractor>(extractor);
    }

    [Fact]
    public void AddPdfDocumentTextExtractor_WithAdiEndpoint_RegistersFallbackExtractor()
    {
        // With DocumentIntelligence:Endpoint present, the method upgrades to a
        // FallbackDocumentTextExtractor (PdfPig primary + ADI secondary).
        // AzureDocumentIntelligenceExtractor's constructor only calls new Uri() +
        // new DefaultAzureCredential() — both succeed at construction time without
        // a live Azure connection, so no live endpoint is required for this DI test.
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DocumentIntelligence:Endpoint"] = "https://fake-adi.cognitiveservices.azure.com/"
            })
            .Build();

        services.AddPdfDocumentTextExtractor(config);

        using var provider = services.BuildServiceProvider();
        var extractor = provider.GetService<IDocumentTextExtractor>();

        Assert.NotNull(extractor);
        Assert.IsType<FallbackDocumentTextExtractor>(extractor);
    }

    [Fact]
    public void AddPdfDocumentTextExtractor_CalledTwice_DoesNotThrowAndResolvesToOneInstance()
    {
        // TryAddSingleton is idempotent — calling AddPdfDocumentTextExtractor a second
        // time (e.g. if AddRagIngestionPipeline also calls it) must not fault and the
        // first registration wins.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPdfDocumentTextExtractor(configuration: null);
        services.AddPdfDocumentTextExtractor(configuration: null); // second call — must be safe

        using var provider = services.BuildServiceProvider();
        var a = provider.GetService<IDocumentTextExtractor>();
        var b = provider.GetService<IDocumentTextExtractor>();

        Assert.NotNull(a);
        Assert.Same(a, b); // same singleton instance
    }
}
