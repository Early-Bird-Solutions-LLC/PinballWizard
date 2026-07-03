namespace PinballWizard.Infrastructure.Jobs;

// Strips CR/LF from a value before it is written to a log message, so a
// route-derived job/execution name cannot forge or split log entries
// (CWE-117 / CodeQL cs/log-forging). ARM resource names never legitimately
// contain line breaks, so this is loss-free for real inputs.
internal static class JobLogSafe
{
    public static string Scrub(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace("\r", "").Replace("\n", "");
}
