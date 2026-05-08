using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Rag.Extraction;

public static class ServiceCollectionExtensions
{
    // Wires the Phase 4 RAG document text extractor into DI. Standalone
    // (no config gate) — the extractor has no runtime state beyond a
    // logger, and is consumed by Wave 2 W2-2 (HybridChunker) and Wave 3
    // W3-2 (Cosmos Change Feed Function). Registered as Singleton
    // because the extractor is stateless + thread-safe (every call
    // opens its own PdfDocument).
    public static IServiceCollection AddPdfDocumentTextExtractor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDocumentTextExtractor, PdfPigDocumentTextExtractor>();
        return services;
    }
}
