namespace PinballWizard.Application.Jobs;

// One expected schedule entry for the #856 dead-man's-switch watchdog.
// BareJobName is the prefix before the 5-char uniqueString suffix
// (e.g. "pinwiz-job-linker", not "pinwiz-job-linker-buutj").
public sealed record ExpectedJobSchedule(
    string BareJobName,
    int MaxGapHours);
