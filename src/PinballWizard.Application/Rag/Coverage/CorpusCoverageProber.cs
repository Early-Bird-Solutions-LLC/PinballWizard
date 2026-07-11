using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Retrieval;

namespace PinballWizard.Application.Rag.Coverage;

// Enumerates each source's live (source × doc-type) cells and, per cell, samples
// one chunk, builds a query from its title + section heading, runs the same
// IRagRetriever the Wizard uses, and asserts a returned chunk belongs to the cell.
// Presence + retrievability only — no LLM. A per-cell failure is recorded as a
// gap with an error note (no masking, invariant #17); the run still completes.
public sealed class CorpusCoverageProber : ICorpusCoverageProber
{
    private const int RetrievalTopK = 10;

    private readonly ICorpusIndexQuery _index;
    private readonly IRagRetriever _retriever;
    private readonly ILogger<CorpusCoverageProber> _logger;

    public CorpusCoverageProber(
        ICorpusIndexQuery index,
        IRagRetriever retriever,
        ILogger<CorpusCoverageProber> logger)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(logger);
        _index = index;
        _retriever = retriever;
        _logger = logger;
    }

    public async Task<CoverageReport> RunAsync(CancellationToken ct)
    {
        var cells = new List<CoverageCell>();
        var sources = new List<SourceFloor>();

        foreach (var source in RagSourceCatalog.All)
        {
            var count = await _index.CountAsync(source, ct).ConfigureAwait(false);
            var isGap = count == 0 && source.ExpectedNonEmpty;
            sources.Add(new SourceFloor(source.SourceId, count, source.ExpectedNonEmpty, isGap));

            if (count == 0)
            {
                if (isGap)
                {
                    _logger.LogWarning(
                        "Coverage source-floor gap: source={Source} has zero indexed chunks.", source.SourceId);
                }
                continue;
            }

            var docTypes = await _index.FacetDocumentTypesAsync(source, ct).ConfigureAwait(false);
            foreach (var dt in docTypes)
            {
                cells.Add(await ProbeCellAsync(source, dt, ct).ConfigureAwait(false));
            }
        }

        var covered = cells.Count(c => c.Retrievable);
        var gaps = cells.Count(c => !c.Retrievable) + sources.Count(s => s.IsGap);
        return new CoverageReport(cells, sources, cells.Count, covered, gaps);
    }

    private async Task<CoverageCell> ProbeCellAsync(RagSource source, DocTypeCount dt, CancellationToken ct)
    {
        var sample = await _index.SampleAsync(source, dt.DocumentType, ct).ConfigureAwait(false);
        if (sample is null)
        {
            return new CoverageCell(source.SourceId, dt.DocumentType, dt.ChunkCount,
                Retrievable: false, SampleDocumentId: string.Empty, Query: string.Empty,
                Error: "no sample chunk returned for cell");
        }

        var query = $"{sample.MachineTitle} {sample.SectionHeading}".Trim();
        try
        {
            var hits = await _retriever
                .RetrieveAsync(query, new RetrievalOptions(TopK: RetrievalTopK), ct)
                .ConfigureAwait(false);
            var retrievable = hits.Any(h =>
                source.Matches(h.DocumentId, h.Manufacturer) &&
                string.Equals(h.DocumentType, dt.DocumentType, StringComparison.Ordinal));
            return new CoverageCell(source.SourceId, dt.DocumentType, dt.ChunkCount,
                retrievable, sample.DocumentId, query, Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Coverage retrieval failed: source={Source} docType={DocType} query={Query}",
                source.SourceId, dt.DocumentType, query);
            return new CoverageCell(source.SourceId, dt.DocumentType, dt.ChunkCount,
                Retrievable: false, sample.DocumentId, query, Error: ex.Message);
        }
    }
}
