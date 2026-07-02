using System.Text.Json;

namespace PinballWizard.Application.Ai.Evaluation.Findability;

// Parses findability probe JSONL files (one FindabilityProbe per line).
// Blank lines and '#' comment lines are skipped (a curator-friendly
// convention so the .jsonl can carry section comments). Malformed lines
// throw with a 1-based line number so a curator can find the bad row quickly.
//
// Lives in Application (not Infrastructure) so both the runner
// (Infrastructure) and tests (test project) can call it without a
// circular dependency — mirrors the pattern of EvalQuestionParser.
public static class FindabilityProbeParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    public static IReadOnlyList<FindabilityProbe> ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Findability probe file not found at '{path}'.", path);
        }

        return Parse(File.ReadAllLines(path), path);
    }

    public static IReadOnlyList<FindabilityProbe> Parse(IEnumerable<string> lines, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);

        var results = new List<FindabilityProbe>();
        var lineNumber = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith('#')) continue;

            FindabilityProbe? probe;
            try
            {
                probe = JsonSerializer.Deserialize<FindabilityProbe>(trimmed, Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Failed to parse {sourceLabel} line {lineNumber}: {ex.Message}", ex);
            }

            if (probe is null)
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} parsed to null (expected a JSON object).");
            }

            if (string.IsNullOrWhiteSpace(probe.Id))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} is missing required field 'id'.");
            }

            if (string.IsNullOrWhiteSpace(probe.Query))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{probe.Id}') is missing required field 'query'.");
            }

            if (probe.ExpectedOpdbIds is null || probe.ExpectedOpdbIds.Count == 0)
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{probe.Id}') must have at least one entry " +
                    "in 'expected_opdb_ids'. A findability probe with no correct answer is undefined.");
            }

            if (!seenIds.Add(probe.Id))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber}: duplicate id '{probe.Id}'.");
            }

            if (probe.Graded is not null)
            {
                foreach (var (opdbId, grade) in probe.Graded)
                {
                    if (grade < 0 || grade > 3)
                    {
                        throw new InvalidDataException(
                            $"{sourceLabel} line {lineNumber} ('{probe.Id}'): grade for '{opdbId}' " +
                            $"is {grade}; must be 0–3.");
                    }
                }
            }

            results.Add(probe);
        }

        return results;
    }
}
