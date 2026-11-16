namespace PinballWizard.Application.Jobs;

public sealed record OverdueJobSchedule(
    string BareJobName,
    string? FullJobName,
    int MaxGapHours,
    double ElapsedHours,
    DateTimeOffset? LastScheduledSuccessUtc);
