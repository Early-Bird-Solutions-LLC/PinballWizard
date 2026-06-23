using PinballWizard.Core.Models;

namespace PinballWizard.Application.Persistence;

// Per-run scrape history. Write-once at run completion; read newest-first per source
// (single-partition, ADR-0036 Tier 1).
public interface IScrapeRunRepository
{
    Task WriteAsync(ScrapeRunRecord record, CancellationToken cancellationToken);

    IAsyncEnumerable<ScrapeRunRecord> StreamBySourceAsync(
        string sourceId, int maxCount, CancellationToken cancellationToken);
}
