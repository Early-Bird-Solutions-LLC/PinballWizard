namespace PinballWizard.Application.Persistence;

public sealed record IngestionSourceRunResult
{
    // Run-completion time. Sets IngestionSource.LastRunAt; also sets
    // LastSuccessAt when Succeeded = true. Pre-existing LastSuccessAt is
    // preserved on a failed run.
    public required DateTimeOffset RunAt { get; init; }

    // True when the run completed without aborting. False on aborted runs;
    // increments TotalRunFailures.
    public required bool Succeeded { get; init; }

    // Documents (machines, scraped items, etc.) discovered or written by
    // this run. Accumulates into TotalDocumentsDiscovered. Pass 0 when
    // the run aborted before producing any output.
    public required int DocumentsDiscovered { get; init; }
}
