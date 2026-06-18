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

    // Valid expected_outcome values (AB#259). A curator typo here would
    // otherwise fall through the harness's outcome dispatch, silently
    // dropping the R2/R3 score (null → excluded from the aggregate
    // denominator) with no error — exactly the failure this set guards.
    private const string OutcomeGrounded = "grounded";
    private const string OutcomeAnsweredAllEditions = "answered_all_editions";
    private const string OutcomeHonestSubstitution = "honest_substitution";

    private static readonly HashSet<string> ValidExpectedOutcomes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            OutcomeGrounded,
            OutcomeAnsweredAllEditions,
            OutcomeHonestSubstitution,
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

            // Edition-aware invariants (AB#259). expected_outcome defaults
            // to "grounded" in the record, so an absent field is fine; a
            // PRESENT-but-typo'd value must fail loudly rather than silently
            // skipping the R2/R3 evaluator.
            if (!ValidExpectedOutcomes.Contains(question.ExpectedOutcome))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{question.Id}') has invalid expected_outcome " +
                    $"'{question.ExpectedOutcome}'. Valid values: {OutcomeGrounded}, {OutcomeAnsweredAllEditions}, {OutcomeHonestSubstitution}.");
            }

            if (string.Equals(question.ExpectedOutcome, OutcomeAnsweredAllEditions, StringComparison.OrdinalIgnoreCase)
                && (question.RequiredEditions is null || question.RequiredEditions.Count == 0))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{question.Id}') has expected_outcome '{OutcomeAnsweredAllEditions}' " +
                    "but no required_editions; the R2 evaluator has nothing to attribute.");
            }

            if (string.Equals(question.ExpectedOutcome, OutcomeHonestSubstitution, StringComparison.OrdinalIgnoreCase)
                && (question.RequiredEditions is null || question.RequiredEditions.Count == 0
                    || string.IsNullOrWhiteSpace(question.RequiredEditions[0])))
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{question.Id}') has expected_outcome '{OutcomeHonestSubstitution}' " +
                    "but no required_editions[0] naming the absent edition the disclosure must reference.");
            }

            // Three-state refusal invariants (AB#259 metric-hygiene fix).
            // refusal_required=true implies acceptable_refusal=true — a row
            // that REQUIRES refusal trivially ACCEPTS it; the contradiction
            // is always a curator typo. And a required-refusal row is
            // out-of-scope by definition, so it cannot carry answer-path
            // ground-truth citations.
            if (question.RefusalRequired && !question.AcceptableRefusal)
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{question.Id}') has refusal_required=true but acceptable_refusal=false. " +
                    "A required refusal is always acceptable — set acceptable_refusal=true.");
            }

            if (question.RefusalRequired && question.ExpectedCitationSet is { Count: > 0 })
            {
                throw new InvalidDataException(
                    $"{sourceLabel} line {lineNumber} ('{question.Id}') has refusal_required=true with a non-empty expected_citation_set. " +
                    "Required-refusal rows are out-of-scope and must not carry answer-path citations.");
            }

            // ExpectedCitationSet is required at the schema level, but
            // tolerate null deserialization (older curator lines that
            // leave it implicit) by substituting an empty list — that
            // matches the refusal-flow semantic.
            var citations = question.ExpectedCitationSet ?? [];
            results.Add(question with { ExpectedCitationSet = citations });
        }

        return results;
    }
}
