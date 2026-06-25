namespace PinballWizard.Core.Models;

// A single scrape/sync run for one ingestion source — the per-run history event
// surfaced on the admin source-detail timeline. Write-once; the persistence id is
// derived deterministically from SourceId + RunAt (see CosmosScrapeRunRepository).
public sealed record ScrapeRunRecord
{
    public required string SourceId { get; init; }
    public required DateTimeOffset RunAt { get; init; }
    public required double DurationSeconds { get; init; }
    public required bool Succeeded { get; init; }
    public required int DocumentsDiscovered { get; init; }
    public int DocumentsNew { get; init; }
    public string? ErrorMessage { get; init; }
    // How the run was invoked ("scheduled" from an ACA job; null = manual/ad-hoc).
    public string? Trigger { get; init; }
}
