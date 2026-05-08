using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Rag.Extraction;

public static class ServiceCollectionExtensions
{
    // Wires the Phase 4 RAG document text extractor into DI. Standalone
    // (no config gate) — the extractor has no runtime state beyond a
    // logger + options, and is consumed by Wave 2 W2-2 (HybridChunker)
    // and Wave 3 W3-2 (Cosmos Change Feed Function). Registered as
    // Singleton because the extractor is stateless + thread-safe (every
    // call opens its own PdfDocument).
    //
    // PdfExtractionOptions defaults (100MB stream size limit, 32-char
    // OCR floor) are sensible for the curated 7-machine subset; the
    // host can override by calling
    // `services.Configure<PdfExtractionOptions>(config.GetSection(
    // PdfExtractionOptions.SectionName))` before this method, mirroring
    // the AddHybridChunker pattern.
    public static IServiceCollection AddPdfDocumentTextExtractor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<PdfExtractionOptions>();
        services.TryAddSingleton<IDocumentTextExtractor, PdfPigDocumentTextExtractor>();
        return services;
    }
}
