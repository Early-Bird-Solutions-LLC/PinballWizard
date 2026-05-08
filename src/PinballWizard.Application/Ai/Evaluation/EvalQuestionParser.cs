using System.Text.Json;

namespace PinballWizard.Application.Ai.Evaluation;

// Parses the JSONL ground-truth file (one EvalQuestion per line) into
// an in-memory list. Blank lines and lines whose first non-whitespace
// character is '#' are skipped (a curator-friendly convention so the
// .jsonl can carry section comments). Malformed lines throw with the
// 1-based line number so a curator can find the bad row quickly.
//
// Lives in Application (not Infrastructure) because the .jsonl shape
// is the public Application contract — the harness (Infrastructure)
// and tests (test project) both need to call it.
public static class EvalQuestionParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    public static IReadOnlyList<EvalQuestion> ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Eval ground-truth file not found at '{path}'. Set EvalHarnessOptions.GroundTruthPath or supply data/eval/wizard.v1.jsonl.",
                path);
        }

        var lines = File.ReadAllLines(path);
        return Parse(lines, path);
    }

    public static IReadOnlyList<EvalQuestion> Parse(IEnumerable<string> lines, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);

        var results = new List<EvalQuestion>();
        var lineNumber = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                // Curator comment line — convention only; not JSON.
                continue;
            }

            EvalQuestion? question;
            try
            {
                question = JsonSerializer.Deserialize<EvalQuestion>(trimmed, Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Failed to parse {sourceLabel} line {lineNumber}: {ex.Message}", ex);
            }

            if (question is null)
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} parsed to null (expected a JSON object).");
            }

            if (string.IsNullOrWhiteSpace(question.Id))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} is missing required field 'id'.");
            }

            if (string.IsNullOrWhiteSpace(question.Question))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{question.Id}') is missing required field 'question'.");
            }

            if (string.IsNullOrWhiteSpace(question.ExpectedSubAgent))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{question.Id}') is missing required field 'expected_sub_agent'.");
            }

            if (!seenIds.Add(question.Id))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber}: duplicate id '{question.Id}'.");
            }

            // ExpectedCitationSet is required at the schema level, but
            // tolerate null deserialization (older curator lines that
            // leave it implicit) by substituting an empty list — that
            // matches the acceptable_refusal=true semantic.
            var citations = question.ExpectedCitationSet ?? [];
            results.Add(question with { ExpectedCitationSet = citations });
        }

        return results;
    }
}
