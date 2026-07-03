namespace PinballWizard.Infrastructure.Jobs;

// Strips CR/LF from a value before it is written to a log message, so a
// route-derived job/execution name cannot forge or split log entries
// (CWE-117 / CodeQL cs/log-forging). ARM resource names never legitimately
// contain line breaks, so this is loss-free for real inputs.
internal static class JobLogSafe
{
    public static string Scrub(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace("\r", "").Replace("\n", "");

    // Escapes a user-supplied value for embedding inside a KQL VERBATIM string
    // literal @'...'. Per Kusto string-literal rules (verified Task 1 against
    // learn.microsoft.com/azure/data-explorer/kusto/query/scalar-data-types/string):
    //   (a) In a verbatim literal @'...', backslash is treated literally (not an
    //       escape character); an embedded single quote is escaped by DOUBLING it ('').
    //   (b) `contains` performs a case-insensitive substring match; the case-sensitive
    //       variant is `contains_cs` (verified from the contains-operator doc page).
    // CR/LF are stripped: a console-search term never legitimately contains them and
    // they could break the single-line query. Length-capping is the caller's job.
    public static string KqlLiteral(string? value) =>
        Scrub(value).Replace("'", "''");
}
