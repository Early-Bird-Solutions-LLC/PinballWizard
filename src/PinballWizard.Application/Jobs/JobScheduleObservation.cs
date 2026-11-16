namespace PinballWizard.Application.Jobs;

// Last successful *scheduled* run observed for one expected job.
// LastScheduledSuccessUtc null means "never had a scheduled success"
// (never ran, only failed, or only manual starts — all overdue).
public sealed record JobScheduleObservation(
    string BareJobName,
    string? FullJobName,
    DateTimeOffset? LastScheduledSuccessUtc);
