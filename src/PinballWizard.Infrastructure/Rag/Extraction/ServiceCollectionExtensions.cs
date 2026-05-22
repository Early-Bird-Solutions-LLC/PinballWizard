using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Rag.Extraction;

public static class ServiceCollectionExtensions
{
    // Wires the document text extractor stack into DI.
    //
    // Without ADI config (Phase 4 / DocumentIntelligence:Endpoint absent):
    //   IDocumentTextExtractor → PdfPigDocumentTextExtractor (unchanged behaviour)
    //
    // With ADI config (Phase 4.5 W1+ / DocumentIntelligence:Endpoint present):
    //   IDocumentTextExtractor → FallbackDocumentTextExtractor
    //     ├── PdfPigDocumentTextExtractor  (primary; concrete singleton)
    //     └── AzureDocumentIntelligenceExtractor  (ADI fallback; concrete singleton)
    //
    // PdfExtractionOptions defaults (100MB stream limit, 32-char OCR floor)
    // are sensible for the full Phase 4.5 corpus; override via
    // services.Configure<PdfExtractionOptions>(...) before this call.
    public static IServiceCollection AddPdfDocumentTextExtractor(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<PdfExtractionOptions>();
        services.TryAddSingleton<PdfPigDocumentTextExtractor>();

        var adiEndpoint = configuration?[DocumentIntelligenceOptions.EndpointKey];
        if (!string.IsNullOrWhiteSpace(adiEndpoint))
        {
            services.AddOptions<DocumentIntelligenceOptions>()
                .Configure(opts => opts.Endpoint = adiEndpoint);
            services.TryAddSingleton<AzureDocumentIntelligenceExtractor>();
            services.TryAddSingleton<IDocumentTextExtractor, FallbackDocumentTextExtractor>();
        }
        else
        {
            services.TryAddSingleton<IDocumentTextExtractor>(sp =>
                sp.GetRequiredService<PdfPigDocumentTextExtractor>());
        }

        return services;
    }
}
