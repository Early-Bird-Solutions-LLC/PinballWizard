namespace PinballWizard.Application.Jobs;

public sealed record JobExecution(
    string ExecutionName,
    string Status,
    DateTimeOffset? StartOn,
    DateTimeOffset? EndOn);
