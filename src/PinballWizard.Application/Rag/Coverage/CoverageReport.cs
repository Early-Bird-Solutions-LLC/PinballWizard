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
    int GapsTotal)
{
    // A cell gap: a live cell whose content was not retrievable.
    public IEnumerable<CoverageCell> CellGaps => Cells.Where(c => !c.Retrievable);

    // A source-floor gap: an ExpectedNonEmpty source with zero chunks.
    public IEnumerable<SourceFloor> SourceGaps => Sources.Where(s => s.IsGap);
}
