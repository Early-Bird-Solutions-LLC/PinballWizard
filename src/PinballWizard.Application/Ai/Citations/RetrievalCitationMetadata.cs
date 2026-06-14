namespace PinballWizard.Application.Ai.Citations;

// UI-only metadata that must reach the citation surface but must NOT
// travel through the model-facing tool-result trace.
//
// SearchCorpusHit keeps Score and LastScrapedUtc as [JsonIgnore]
// properties — the model must never see retrieval internals such as
// relevance scores or freshness timestamps (it might use them for
// meta-reasoning rather than actual content reasoning). That deliberate
// [JsonIgnore] means those values are stripped from the
// FunctionResultContent.Result JSON that ToolTraceCitationExtractor
// reads on the real Foundry path, arriving as null despite being
// populated on the typed C# object.
//
// The fix is a request-scoped side channel: SearchCorpusTool records
// UI metadata here by DocumentUrl immediately after building each hit;
// ToolTraceCitationExtractor enriches each corpus Citation from the
// sink when the typed C# fields are null (the JSON-path case).
//
// Keyed by DocumentUrl with FIRST-WRITE-WINS semantics, matching the
// dedup-by-DocumentUrl logic in ToolTraceCitationExtractor
// (AddCitationsFromCorpusHits) — the first and typically highest-ranked
// hit per URL is the one whose metadata reaches the citation card.
public sealed record RetrievalCitationMetadata(
    DateTimeOffset? LastScrapedUtc,
    double? RelevanceScore);

// Request-scoped store for UI-only retrieval metadata that cannot
// travel through the model-facing tool-result trace.
//
// Scoped (one instance per HTTP request / per streaming turn). The
// SearchCorpusTool records into it during retrieval; the
// ToolTraceCitationExtractor reads from it during citation assembly.
// This decoupling keeps the model-facing JSON clean while allowing
// the citation surface to carry freshness + relevance.
public interface IRetrievalCitationMetadataSink
{
    // Records UI metadata for a given document URL. First-write-wins
    // per URL — subsequent writes for the same URL are silently ignored.
    // Callers should skip when documentUrl is null or whitespace.
    void Record(string documentUrl, RetrievalCitationMetadata metadata);

    // Returns true and sets metadata when the sink contains an entry for
    // documentUrl (case-insensitive). Returns false with metadata = null
    // when the URL is not present (pre-C3 chunks, or the sink was not
    // wired because the SearchCorpusTool ran without one injected).
    bool TryGet(string documentUrl, out RetrievalCitationMetadata? metadata);
}

// Concrete request-scoped implementation backed by a Dictionary.
//
// Not thread-safety-critical: the sink is request-scoped and the
// citation assembly flow is a single logical sequence (retrieval
// → agent turn → extractor). If that ever changes (e.g., parallel
// sub-agent retrieval), swap to ConcurrentDictionary here.
public sealed class RetrievalCitationMetadataSink : IRetrievalCitationMetadataSink
{
    private readonly Dictionary<string, RetrievalCitationMetadata> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(string documentUrl, RetrievalCitationMetadata metadata)
    {
        // First-write-wins: the first (highest-ranked) hit per URL sets
        // the metadata. Later hits for the same document are collapsed
        // by the citation dedup in ToolTraceCitationExtractor anyway.
        _store.TryAdd(documentUrl, metadata);
    }

    public bool TryGet(string documentUrl, out RetrievalCitationMetadata? metadata)
        => _store.TryGetValue(documentUrl, out metadata);
}
