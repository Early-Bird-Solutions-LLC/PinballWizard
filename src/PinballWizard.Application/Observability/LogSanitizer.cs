namespace PinballWizard.Application.Observability;

// Sanitizes user-supplied free text before it enters a log message. Raw text
// from a public endpoint would allow log forging (CWE-117): a newline-laden
// value could inject fake log lines. Strip control characters and cap the
// length; sanitized values are diagnostic only. Shared by every call site
// that logs query text (MachineSuggestService, AiSearchMachineIndex,
// GridSearchService) so the sanitization rules cannot drift apart.
public static class LogSanitizer
{
    private const int MaxLength = 120;

    public static string ForLog(string value)
    {
        var cleaned = new string(value.Where(static c => !char.IsControl(c)).ToArray());
        return cleaned.Length > MaxLength ? cleaned[..MaxLength] + "…" : cleaned;
    }
}
