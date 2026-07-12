namespace PinballWizard.Application.Rag.Coverage;

public interface ICorpusCoverageProber
{
    Task<CoverageReport> RunAsync(CancellationToken ct);
}
