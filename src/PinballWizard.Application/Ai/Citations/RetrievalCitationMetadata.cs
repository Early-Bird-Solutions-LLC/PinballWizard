using System.Collections.Concurrent;

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
// The fix is a process-wide side channel: SearchCorpusTool records
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

// Process-wide side channel for UI-only retrieval metadata that cannot
// travel through the model-facing tool-result trace.
//
// Singleton, shared between two singleton consumers: SearchCorpusTool
// records into it during retrieval; ToolTraceCitationExtractor (held by the
// singleton AiRouter) reads from it during citation assembly. Both are
// app-lifetime singletons, so the channel they share MUST also be a singleton
// — a scoped registration is a captive dependency that the DI scope validator
// rejects in Development and that silently degraded to a root-captured
// singleton in Production anyway. This decoupling keeps the model-facing JSON
// clean while letting the citation surface carry freshness + relevance.
//
// The store is keyed by DocumentUrl with first-write-wins, so it is bounded by
// the corpus size (not per-request unbounded), and LastScrapedUtc is stable
// per URL. The one consequence of sharing across requests: RelevanceScore is
// query-specific, so a URL seen by an earlier query keeps that query's score
// on the citation card (a cosmetic, UI-only staleness — never a provenance or
// answer-correctness issue). True per-turn isolation would require making the
// whole tool/extractor/router chain scoped — an accepted limitation (not yet
// tracked); revisit if the citation surface ever renders RelevanceScore.
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

// Concrete singleton implementation backed by a ConcurrentDictionary.
//
// Thread-safe because, as a singleton, this instance is shared across all
// concurrent requests (and across the retrieval / agent-turn / extractor flow
// within each). ConcurrentDictionary.TryAdd preserves the first-write-wins
// semantics the citation dedup relies on without a lock.
public sealed class RetrievalCitationMetadataSink : IRetrievalCitationMetadataSink
{
    private readonly ConcurrentDictionary<string, RetrievalCitationMetadata> _store =
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
