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

    // True when a retrieved chunk is retrievable from the perspective of a user
    // query — used at retrieval-check time in CorpusCoverageProber only.
    //
    // For manufacturer-backed sources the doc_ prefix is an index-scoping aid
    // for SampleAsync/CountAsync (native scraped documents only), not a
    // user-visible boundary.  TiltForums and Kineticist chunks carry the game's
    // manufacturer value, so a user querying "Elton John rules" WILL receive
    // those chunks — the probe should not report a gap just because the top-10
    // results happen to be tiltforums_*/kineticist_* rather than doc_*.
    //
    // Prefix-only (synthesized) sources have no manufacturer value and must
    // still be identified by their document_id prefix.
    public bool MatchesRetrieval(string documentId, string manufacturer)
    {
        if (ManufacturerValues.Count > 0)
        {
            // Manufacturer-backed: the manufacturer value alone is the
            // user-visible boundary.  Skip the DocumentIdPrefix requirement.
            return ManufacturerValues.Contains(manufacturer, StringComparer.Ordinal);
        }

        // Prefix-only (synthesized: Kineticist, TiltForums, TWIP, …): the
        // document_id prefix is the only reliable identifier.
        if (DocumentIdPrefix is not null)
        {
            return documentId.StartsWith(DocumentIdPrefix, StringComparison.Ordinal);
        }

        // Neither prefix nor manufacturer set — fail open rather than produce
        // an unconditional miss (should not occur in a well-formed catalog).
        return true;
    }
}
