namespace PinballWizard.Application.OpsTriage;

/// <summary>In-process queue from webhook accept → background processor.</summary>
public interface IOpsTriageWorkChannel
{
    ValueTask EnqueueAsync(string triageRunId, string alertDay, CancellationToken cancellationToken);

    /// <summary>Blocks until a work item is available or the token cancels.</summary>
    ValueTask<(string TriageRunId, string AlertDay)> DequeueAsync(CancellationToken cancellationToken);
}
