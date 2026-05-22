using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Application.Rag.Extraction;

// Configuration for the Azure Document Intelligence OCR fallback
// (Phase 4.5 W1). Registered and consumed only when the Endpoint
// key is present — same conditional-registration pattern as
// AiSearchOptions and AiFoundryOptions.
public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";
    public const string EndpointKey = "DocumentIntelligence:Endpoint";

    // Azure Document Intelligence endpoint URI, e.g.
    // https://pinwiz-docint-dev-xxxxx.cognitiveservices.azure.com/
    // Absent → FallbackDocumentTextExtractor is not registered and
    // OcrRequired documents are logged + skipped as in Phase 4.
    [Required]
    public string Endpoint { get; set; } = string.Empty;
}
