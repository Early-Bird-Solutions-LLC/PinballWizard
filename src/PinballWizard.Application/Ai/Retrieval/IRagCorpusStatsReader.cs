namespace PinballWizard.Application.Ai.Retrieval;

// Read-only corpus-level statistics for the RAG index (Azure AI Search). Surfaced
// by the /admin/corpus page. Implementations read the live index; a failure to reach
// it throws (the page degrades visibly — Invariant #17 — rather than reporting zeros).
public interface IRagCorpusStatsReader
{
    Task<RagCorpusStats> GetCorpusStatsAsync(CancellationToken cancellationToken);
}

// A point-in-time snapshot of the RAG corpus.
//   TotalChunks         — total indexed chunks in the index (one chunk != one document).
//   ByDocumentType      — indexed-chunk count per document_type, descending by count.
//   MostRecentScrapeUtc — newest source-document scrape present in the index (content
//                         freshness); null when the corpus is empty or only holds
//                         pre-backfill chunks lacking last_scraped_utc.
public sealed record RagCorpusStats(
    long TotalChunks,
    IReadOnlyList<DocTypeChunkCount> ByDocumentType,
    DateTimeOffset? MostRecentScrapeUtc);

public sealed record DocTypeChunkCount(string DocumentType, long ChunkCount);
