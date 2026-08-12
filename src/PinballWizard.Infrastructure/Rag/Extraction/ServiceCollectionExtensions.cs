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

        // #832: the preview interface always maps to the PdfPig singleton,
        // in BOTH branches. Only PdfPig can honor a page/memory bound (ADI's
        // ReadToBytesAsync materializes the whole blob before its page-range
        // parameter limits anything), and the fallback decorator would never
        // route a preview to ADI anyway (it fires only on OcrRequired, which
        // the preview path never returns). Registered here — not in each
        // branch — so "extraction module present ⇒ preview resolvable" is
        // structural. DocumentLinker resolves this with GetService; a missed
        // registration would disable page tiers silently (see
        // ExtractionServiceCollectionTests).
        services.TryAddSingleton<IDocumentPreviewExtractor>(sp =>
            sp.GetRequiredService<PdfPigDocumentTextExtractor>());

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
