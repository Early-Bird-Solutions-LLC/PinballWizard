namespace PinballWizard.Application.Rag.Coverage;

// One ingestion source and how to recognise its chunks in the RAG index.
// "Source" is NOT the same as the index `manufacturer` field: synthesized
// content (Kineticist/TiltForums/PB Freshdesk) carries the game's manufacturer,
// so those sources are identified by their document_id prefix instead. Scraped
// manufacturers are identified by manufacturer value AND the `doc_` prefix, so a
// Kineticist-for-Stern chunk (manufacturer="Stern", id="kineticist_…") is not
// misattributed to the Stern scraper.
public sealed record RagSource(
    string SourceId,
    IReadOnlyList<string> ManufacturerValues,
    string? DocumentIdPrefix,
    bool ExpectedNonEmpty)
{
    // True when a retrieved chunk belongs to this source. Used to verify a
    // retrieval hit came from the cell under test.
    // Precondition: at least one of DocumentIdPrefix or ManufacturerValues must be set,
    // or Matches returns true for every chunk (no filtering applied).
    public bool Matches(string documentId, string manufacturer)
    {
        if (DocumentIdPrefix is not null &&
            !documentId.StartsWith(DocumentIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (ManufacturerValues.Count > 0 &&
            !ManufacturerValues.Contains(manufacturer, StringComparer.Ordinal))
        {
            return false;
        }

        return true;
    }
}
