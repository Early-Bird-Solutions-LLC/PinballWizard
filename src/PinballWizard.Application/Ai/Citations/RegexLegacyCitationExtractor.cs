using System.Text.RegularExpressions;
using Microsoft.Agents.AI;

namespace PinballWizard.Application.Ai.Citations;

// Phase 3 citation extractor: scans the agent's final response text for
// OPDB machine URLs via regex. Retained behind AiFoundryOptions
// .RetainRegexCitationCutover for the cutover observability window per ADR-0022
// § Telemetry — its citation count is emitted under
// pinwiz.ai.citations.extracted_total{source=regex_legacy} alongside
// the new ToolTraceCitationExtractor's tool_trace count, so a behavioral
// regression between the two would be visible before H3 rerun.
//
// AiRouter does NOT use this extractor's output for the WizardAnswer's
// Citations field once the new tool-trace extractor is the primary —
// only its count flows into telemetry. After H2 baseline confirms the
// tool-trace extractor produces equal-or-better citation_precision,
// this class + the cutover flag get deleted in a follow-up PR.
public sealed partial class RegexLegacyCitationExtractor : ICitationExtractor
{
    [GeneratedRegex(@"https://opdb\.org/machines/(?<id>[A-Z0-9\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpdbMachineUrlRegex();

    public string SourceTag => "regex_legacy";

    public IReadOnlyList<Citation> Extract(AgentResponse? response)
    {
        var text = response?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<Citation>();
        }

        var matches = OpdbMachineUrlRegex().Matches(text);
        if (matches.Count == 0)
        {
            return Array.Empty<Citation>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var citations = new List<Citation>(matches.Count);
        foreach (Match match in matches)
        {
            var url = match.Value;
            if (!seen.Add(url))
            {
                continue;
            }

            var opdbId = match.Groups["id"].Value;
            citations.Add(new Citation(
                Title: $"OPDB record {opdbId}",
                SourceUrl: url,
                MachineId: opdbId,
                DocumentChunkId: null));
        }

        return citations;
    }
}
