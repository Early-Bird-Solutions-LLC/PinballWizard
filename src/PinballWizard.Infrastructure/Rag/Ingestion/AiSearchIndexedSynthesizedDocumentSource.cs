using System.Runtime.CompilerServices;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Infrastructure.Rag.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Default `IIndexedSynthesizedDocumentSource`. Enumerates the RAG search index once
// and yields each distinct SYNTHESIZED document (Kineticist / Tilt Forums / TWIP /
// PB-Freshdesk, identified by the doc-id prefixes in SynthesizedSourceDescriptors).
//
// Unlike AiSearchIndexedPairSource (which projects only document_id + machine_id for
// the orphan GC), this projects the fuller provenance field set — plus `content`, so
// the human title can be recovered from each document's leading "# {title}" header.
// The 3072-d embedding is still never projected. Projecting `content` for the whole
// scan is more bandwidth than the pair scan, but this is a one-off admin/maintenance
// read (the --backfill-synthesized-raw-docs verb), not a hot path, so a single scan
// is preferred over N per-document content look-ups.
//
// Documents are buffered (not streamed incrementally) because the title lives on
// whichever chunk carries the "# " header, which may not be the first chunk seen —
// only synthesized documents are buffered, a small subset of the index.
public sealed class AiSearchIndexedSynthesizedDocumentSource : IIndexedSynthesizedDocumentSource
{
    private readonly SearchClient _searchClient;
    private readonly ILogger<AiSearchIndexedSynthesizedDocumentSource> _logger;

    public AiSearchIndexedSynthesizedDocumentSource(
        SearchClient searchClient,
        ILogger<AiSearchIndexedSynthesizedDocumentSource> logger)
    {
        ArgumentNullException.ThrowIfNull(searchClient);
        ArgumentNullException.ThrowIfNull(logger);
        _searchClient = searchClient;
        _logger = logger;
    }

    public async IAsyncEnumerable<IndexedSynthesizedDocument> StreamSynthesizedDocumentsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = new SearchOptions();
        options.Select.Add(AiSearchIndexFields.DocumentId);
        options.Select.Add(AiSearchIndexFields.MachineId);
        options.Select.Add(AiSearchIndexFields.MachineTitle);
        options.Select.Add(AiSearchIndexFields.Manufacturer);
        options.Select.Add(AiSearchIndexFields.DocumentUrl);
        options.Select.Add(AiSearchIndexFields.DocumentType);
        options.Select.Add(AiSearchIndexFields.LastScrapedUtc);
        options.Select.Add(AiSearchIndexFields.Content);

        var response = await _searchClient
            .SearchAsync<SearchDocument>(searchText: "*", options, cancellationToken)
            .ConfigureAwait(false);

        var byDocument = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var chunks = 0;
        await foreach (var result in response.Value.GetResultsAsync().ConfigureAwait(false))
        {
            chunks++;
            var doc = result.Document;

            if (!TryGetString(doc, AiSearchIndexFields.DocumentId, out var documentId)
                || SynthesizedSourceDescriptors.ForDocumentId(documentId) is null)
            {
                // Not a synthesized document (e.g. a scraped "doc_" chunk) — out of scope.
                continue;
            }

            if (!byDocument.TryGetValue(documentId, out var acc))
            {
                TryGetString(doc, AiSearchIndexFields.MachineId, out var machineId);
                TryGetString(doc, AiSearchIndexFields.MachineTitle, out var machineTitle);
                TryGetString(doc, AiSearchIndexFields.Manufacturer, out var manufacturer);
                TryGetString(doc, AiSearchIndexFields.DocumentUrl, out var documentUrl);
                TryGetString(doc, AiSearchIndexFields.DocumentType, out var documentType);
                DateTimeOffset? lastScraped = null;
                if (doc.TryGetValue(AiSearchIndexFields.LastScrapedUtc, out var lsRaw)
                    && lsRaw is DateTimeOffset dto)
                {
                    lastScraped = dto;
                }
                acc = new Accumulator(machineId, machineTitle, manufacturer, documentUrl, documentType, lastScraped);
                byDocument[documentId] = acc;
            }

            // Recover the human title from the chunk whose content leads with "# {title}".
            if (acc.Title is null
                && TryGetString(doc, AiSearchIndexFields.Content, out var content))
            {
                acc.Title = TryParseTitle(content);
            }
        }

        _logger.LogInformation(
            "Synthesized index scan: {DistinctDocuments} distinct synthesized documents across {ChunkCount} chunks.",
            byDocument.Count, chunks);

        foreach (var (documentId, acc) in byDocument)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new IndexedSynthesizedDocument(
                DocumentId: documentId,
                MachineId: acc.MachineId,
                MachineTitle: acc.MachineTitle,
                Manufacturer: acc.Manufacturer,
                DocumentUrl: acc.DocumentUrl,
                DocumentTypeName: acc.DocumentType,
                LastScrapedUtc: acc.LastScrapedUtc,
                Title: acc.Title);
        }
    }

    // Parses "# {title}\n…" → "{title}". Returns null when the content does not lead
    // with a markdown H1 (a mid-article chunk whose leading header was sliced off).
    internal static string? TryParseTitle(string content)
    {
        if (string.IsNullOrEmpty(content) || !content.StartsWith("# ", StringComparison.Ordinal))
        {
            return null;
        }
        var newline = content.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = newline >= 0 ? content[..newline] : content;
        var title = firstLine[2..].Trim();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    private static bool TryGetString(SearchDocument doc, string field, out string value)
    {
        if (doc.TryGetValue(field, out var raw) && raw is string s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private sealed class Accumulator(
        string machineId,
        string machineTitle,
        string manufacturer,
        string documentUrl,
        string documentType,
        DateTimeOffset? lastScrapedUtc)
    {
        public string MachineId { get; } = machineId;
        public string MachineTitle { get; } = machineTitle;
        public string Manufacturer { get; } = manufacturer;
        public string DocumentUrl { get; } = documentUrl;
        public string DocumentType { get; } = documentType;
        public DateTimeOffset? LastScrapedUtc { get; } = lastScrapedUtc;
        public string? Title { get; set; }
    }
}
