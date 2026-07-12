namespace PinballWizard.Application.Rag.Coverage;

// A single (source × document_type) cell result.
public sealed record CoverageCell(
    string Source,
    string DocumentType,
    long ChunkCount,
    bool Retrievable,
    string SampleDocumentId,
    string Query,
    string? Error);

// A source-level presence result (the "source floor").
public sealed record SourceFloor(
    string Source,
    long ChunkCount,
    bool ExpectedNonEmpty,
    bool IsGap);

public sealed record CoverageReport(
    IReadOnlyList<CoverageCell> Cells,
    IReadOnlyList<SourceFloor> Sources,
    int CellsTotal,
    int CellsCovered,
    int GapsTotal,
    int RetrievabilityWarnings)
{
    // Hard gaps: an ExpectedNonEmpty source with zero indexed chunks.
    public IEnumerable<SourceFloor> SourceGaps => Sources.Where(s => s.IsGap);
    // Soft warnings: a live cell whose sample content wasn't retrievable (the
    // auto-derived query is an imperfect proxy — reported, not gated).
    public IEnumerable<CoverageCell> Warnings => Cells.Where(c => !c.Retrievable);
}
