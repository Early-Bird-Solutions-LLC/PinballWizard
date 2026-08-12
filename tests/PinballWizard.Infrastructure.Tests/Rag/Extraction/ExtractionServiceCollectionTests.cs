using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Rag.Extraction;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Extraction;

// #832 DI-resolvability gate. The linker resolves IDocumentPreviewExtractor
// with GetService (optional — scraper-only CLI mode legitimately runs without
// extraction wiring), so a missed registration fails SILENTLY: startup does
// not throw, unit tests construct fakes directly and stay green, and in
// production every page-tier document quietly falls to not_in_catalog. These
// tests make "extraction module registered ⇒ preview resolvable" an invariant.
public sealed class ExtractionServiceCollectionTests
{
    private static ServiceProvider Build(bool withAdiEndpoint)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(withAdiEndpoint
                ? new Dictionary<string, string?> { [DocumentIntelligenceOptions.EndpointKey] = "https://adi.example.invalid/" }
                : [])
            .Build();
        services.AddPdfDocumentTextExtractor(config);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewExtractor_Resolves_InBothBranches(bool withAdiEndpoint)
    {
        using var sp = Build(withAdiEndpoint);

        var preview = sp.GetService<IDocumentPreviewExtractor>();

        Assert.NotNull(preview);
        Assert.IsType<PdfPigDocumentTextExtractor>(preview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewExtractor_IsSamePdfPigSingletonAsConcreteRegistration(bool withAdiEndpoint)
    {
        using var sp = Build(withAdiEndpoint);

        var preview = sp.GetRequiredService<IDocumentPreviewExtractor>();
        var concrete = sp.GetRequiredService<PdfPigDocumentTextExtractor>();

        Assert.Same(concrete, preview);
    }
}
