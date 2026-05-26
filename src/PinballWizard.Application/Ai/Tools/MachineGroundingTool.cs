using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tool exposed to all four Wave-2 agents per ADR-0014.
// The Microsoft Agent Framework's AIFunctionFactory.Create wraps the
// GetMachineByTitleAsync method into an AIFunction that the agent can
// invoke on demand; [Description] attributes flow into the JSON-Schema
// the model sees, so the agent prompt does not need to repeat the
// argument shape.
//
// PR 5 of the Cosmos for User Delight track (ADR-0025 § 4) replaced
// the cross-partition `STRINGEQUALS` query in this tool with a two-
// point-read path: first the `machine_title_lookups` materialized
// view to resolve title → (opdb_id, manufacturer), then the `machines`
// container to fetch the actual record. The original cross-partition
// query survives as a logged-warning fallback for the unmigrated-
// lookup case (post-deploy backfill pending, transient lookup-write
// failure during OPDB sync, etc.) so the tool degrades gracefully
// rather than refusing to answer.
//
// S5 (ADR-0029): after resolving the primary machine, if it carries a
// GroupId the tool fetches all sibling base-machine records sharing the
// same leading OPDB segment and includes them in the returned DTO.
// Siblings let the agent ask one targeted clarifying question
// (2–3 options) when the question is version-dependent (repair /
// rules-detail / price) without fabricating edition differences.
public sealed class MachineGroundingTool
{
    private readonly IMachineRepository _machines;
    private readonly IMachineTitleLookupRepository _titleLookups;
    private readonly ILogger<MachineGroundingTool> _logger;

    public MachineGroundingTool(
        IMachineRepository machines,
        IMachineTitleLookupRepository titleLookups,
        ILogger<MachineGroundingTool> logger)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(titleLookups);
        ArgumentNullException.ThrowIfNull(logger);
        _machines = machines;
        _titleLookups = titleLookups;
        _logger = logger;
    }

    // Tool tag value emitted on `pinwiz.ai.tool_duration_ms` and (when the
    // tool gains a catch boundary) `pinwiz.ai.tool_errors_total`. Matches
    // the JSON-Schema function name the Microsoft Agent Framework derives
    // from this method, so dashboards and prompts agree on the label.
    internal const string ToolTagValue = "getMachineByTitle";

    // Stop-words filtered out before token-overlap scoring in
    // RefusalRecoveryService. Kept here (not in the recovery service)
    // because this class is the canonical tokenization authority for
    // machine-title lookups — both the grounding tool and the recovery
    // service operate on the same vocabulary.
    // Lowercase only; TokenizeForOverlap lowercases input before splitting.
    internal static readonly HashSet<string> TokenStopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "in", "on", "at", "to",
        "for", "with", "by", "from", "is", "it", "its", "this", "that",
        "my", "me", "i", "what", "how", "when", "where", "which", "who",
        "can", "could", "would", "should", "will", "do", "does", "did",
        "be", "been", "was", "were", "are", "am", "has", "have", "had",
        "not", "no", "but", "if", "as", "so", "up", "out", "about",
        "into", "than", "more", "other", "after", "before", "just",
        "tell", "know", "get", "give", "show", "find", "want",
    };

    // Tokenizes a free-text string for overlap scoring against machine
    // titles. Returns normalized, meaningful tokens — stop-words and
    // single-character tokens are excluded. Numerics are retained (e.g.,
    // "007", "2001") because they are often load-bearing in machine names.
    //
    // Splitting on whitespace and common punctuation (',', '.', '!', '?',
    // '(', ')', '-', '/', '\'', '"') produces tokens that match the
    // subword vocabulary of typical machine titles.
    //
    // `internal` (not private) so RefusalRecoveryService and tests can
    // share the exact same tokenizer without duplicating the logic.
    internal static IReadOnlyList<string> TokenizeForOverlap(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var lower = text.ToLowerInvariant();

        // Split on whitespace and punctuation.
        var raw = lower.Split(
            [' ', '\t', ',', '.', '!', '?', '(', ')', '-', '/', '\'', '"', ':', ';'],
            StringSplitOptions.RemoveEmptyEntries);

        var result = new List<string>(raw.Length);
        foreach (var tok in raw)
        {
            if (tok.Length <= 1) continue;
            if (TokenStopWords.Contains(tok)) continue;
            result.Add(tok);
        }

        return result;
    }

    // Scores a collision-row entry (manufacturer key) against tokens extracted
    // from the user-supplied title string. Returns an integer score: +1 per
    // matching token. Zero means no signal — used as a tie-break sentinel to
    // preserve insertion-order behaviour when the input carries no manufacturer
    // qualifier (e.g. bare "Godzilla" scores 0 for all entries, so the first
    // entry wins as before).
    //
    // MachineTitleLookup stores only OpdbIds + Manufacturers (no year column),
    // so year disambiguation would require extending the lookup schema. Scoring
    // is manufacturer-token-only for now; "Stern Godzilla" correctly resolves
    // Stern over Sega via the "stern" token match.
    internal static int ScoreEntryAgainstTokens(
        string manufacturerKey,
        IReadOnlyList<string> titleTokens)
    {
        var score = 0;
        foreach (var token in titleTokens)
        {
            if (string.Equals(token, manufacturerKey, StringComparison.Ordinal))
                score++;
        }
        return score;
    }

    [Description("Look up a pinball machine by its title (case-insensitive). Returns the manufacturer, year, themes, designers, editions, OPDB source URL, and — when the machine belongs to a multi-edition group — sibling base-machine records sharing the same OPDB group so the agent can ask a targeted clarifying question for version-dependent topics. Returns null if no machine matches the title.")]
    public async Task<MachineGroundingDto?> GetMachineByTitleAsync(
        [Description("The pinball-machine title to look up, case-insensitive. Include the manufacturer name if the user stated it (for example: 'Stern Godzilla', 'Foo Fighters', 'Attack from Mars Remake'). The manufacturer qualifier resolves ambiguity when multiple machines share the same franchise title (e.g. Sega vs Stern Godzilla). Omit edition suffixes like Pro/Premium/LE — those are resolved via the returned Siblings list.")] string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Point-read path per ADR-0025 § 4 — `machine_title_lookups`
            // resolves title → (opdb_id, manufacturer), then `machines`
            // is point-read by (id, partitionKey). Two ~5ms reads vs the
            // pre-PR-5 ~50-150ms cross-partition query.
            var lookup = await _titleLookups.GetByTitleAsync(title, cancellationToken).ConfigureAwait(false);
            Machine? match = null;
            var lookupHit = lookup is not null && lookup.OpdbIds.Count > 0;

            // Guard: OpdbIds and Manufacturers must be the same length (maintained
            // by UpsertEntry / RemoveEntry). A mismatch indicates data corruption
            // (direct Cosmos edit, buggy migration, or partial write). Degrade to
            // the cross-partition fallback so the user query still resolves; the
            // next OPDB sync will rewrite the row correctly.
            if (lookupHit && lookup!.OpdbIds.Count != lookup.Manufacturers.Count)
            {
                _logger.LogWarning(
                    "MachineGroundingTool: lookup row for '{Title}' has mismatched OpdbIds ({OpdbCount}) and Manufacturers ({ManufacturerCount}) — possible data corruption. Falling back to cross-partition query. Re-run OPDB sync to remediate.",
                    title, lookup.OpdbIds.Count, lookup.Manufacturers.Count);
                lookupHit = false;
            }

            if (lookupHit)
            {
                // Score every collision-row entry against manufacturer tokens
                // extracted from the input title. The highest-scoring entry is
                // resolved first; ties (all-zero or equal scores) preserve
                // insertion order — backward-compatible with the pre-scoring
                // first-hit behaviour for bare franchise titles ("Godzilla").
                var titleTokens = TokenizeForOverlap(title);
                var bestIdx = 0;
                var bestScore = ScoreEntryAgainstTokens(lookup!.Manufacturers[0], titleTokens);

                for (var i = 1; i < lookup.OpdbIds.Count; i++)
                {
                    var score = ScoreEntryAgainstTokens(lookup.Manufacturers[i], titleTokens);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }

                var opdbId = lookup.OpdbIds[bestIdx];
                var manufacturer = lookup.Manufacturers[bestIdx];
                match = await _machines.GetByOpdbIdAsync(opdbId, manufacturer, cancellationToken).ConfigureAwait(false);

                if (match is null)
                {
                    _logger.LogWarning(
                        "MachineGroundingTool: lookup '{Title}' pointed at opdb_id '{OpdbId}' / manufacturer '{Manufacturer}' (score={Score}) but the machine row is missing. Falling back to cross-partition QueryByTitleAsync. Stale lookup will self-correct on the next OPDB sync.",
                        title, opdbId, manufacturer, bestScore);
                }
            }

            if (match is null)
            {
                // Cross-partition fallback — the pre-PR-5 path. Retained
                // so the tool degrades gracefully when:
                //   • The lookup container hasn't been backfilled yet
                //     (post-deploy, before the first OPDB sync runs).
                //   • A transient lookup-write failure during OPDB sync
                //     left a gap.
                //   • A title-rename happened between the lookup read
                //     and the machine read (rare race).
                // If a fallback fires AND the lookup row was missing
                // entirely (not stale), log a warning so operators see
                // the gap and can decide whether to re-run the OPDB
                // sync. A stale lookup (lookupHit=true but machine row
                // gone) was already logged above — don't double-log.
                await foreach (var machine in _machines.QueryByTitleAsync(title, cancellationToken).ConfigureAwait(false))
                {
                    match = machine;
                    break;
                }

                if (match is not null && !lookupHit)
                {
                    _logger.LogWarning(
                        "MachineGroundingTool: title '{Title}' resolved via fallback cross-partition query because the lookup row is missing. Operator action: confirm OPDB sync has run since the last deploy. The lookup will populate on the next sync.",
                        title);
                }
            }

            if (match is null)
            {
                _logger.LogDebug("MachineGroundingTool: no match for title '{Title}'.", title);
                return null;
            }

            var editions = ProjectEditions(match.Editions);

            // S5 (ADR-0029): fetch sibling base records when the resolved
            // machine has a GroupId. Siblings let the agent ask a single
            // targeted clarifying question (2–3 options max) for version-
            // dependent questions without fabricating edition differences.
            // Best-effort: a failure here must NOT abort the primary answer.
            var siblings = await ResolveSiblingsAsync(match, cancellationToken).ConfigureAwait(false);

            return new MachineGroundingDto(
                OpdbId: match.Id,
                Title: match.Title,
                Manufacturer: match.ManufacturerDisplayName,
                Year: match.Year,
                Themes: match.Themes.AsReadOnly(),
                Designers: match.Designers.AsReadOnly(),
                OpdbSourceUrl: match.OpdbSourceUrl,
                Editions: editions,
                GroupId: match.GroupId,
                Siblings: siblings);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", ToolTagValue));
        }
    }

    private async Task<IReadOnlyList<MachineSiblingGroundingDto>> ResolveSiblingsAsync(
        Machine primary,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(primary.GroupId))
            return [];

        var siblings = new List<MachineSiblingGroundingDto>();
        try
        {
            await foreach (var sibling in _machines
                .GetSiblingsByGroupIdAsync(primary.GroupId, cancellationToken)
                .ConfigureAwait(false))
            {
                // Exclude the primary machine from the sibling list —
                // the caller already has it as the top-level result.
                if (sibling.Id == primary.Id)
                    continue;

                siblings.Add(new MachineSiblingGroundingDto(
                    OpdbId: sibling.Id,
                    Title: sibling.Title,
                    Year: sibling.Year,
                    Editions: ProjectEditions(sibling.Editions)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sibling fetch is best-effort. A Cosmos failure here must
            // not prevent the agent from grounding the primary machine —
            // it degrades to single-machine mode (no clarifying question
            // for version-dependent topics). Logged at Warning so the gap
            // surfaces on dashboards without polluting Error budgets.
            _logger.LogWarning(ex,
                "MachineGroundingTool: sibling fetch for GroupId '{GroupId}' (primary '{OpdbId}') failed. Returning empty sibling list.",
                primary.GroupId, primary.Id);
        }

        return siblings;
    }

    private static List<MachineEditionGroundingDto> ProjectEditions(
        IEnumerable<MachineEdition> editions) =>
        editions
            .Select(e => new MachineEditionGroundingDto(
                Name: e.Name,
                Msrp: e.Msrp,
                Availability: e.Availability,
                Description: e.Description))
            .ToList();
}