namespace PinballWizard.Application.Jobs;

public enum JobLogSeverity { Info, Warning, Error, Unknown }

public sealed record JobLogLine(DateTimeOffset Timestamp, string Message, JobLogSeverity Severity);
