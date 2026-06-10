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

    // Scores a collision-row entry (stored MatchTokens) against tokens extracted
    // from the user-supplied title string. Returns an integer score: +1 per
    // titleToken that appears in matchTokens (each titleToken counted at most
    // once per entry). Zero means no signal — used as a tie-break sentinel to
    // preserve insertion-order behaviour when the input carries no manufacturer
    // qualifier (e.g. bare "Godzilla" scores 0 for all entries, so the first
    // entry wins as before).
    //
    // Using the stored MatchTokens (e.g. ["jjp", "jersey", "jack"]) rather than
    // the raw manufacturer key ("jjp") means expanded display names like
    // "Jersey Jack Pirates" now resolve correctly to JJP entries.
    internal static int ScoreEntryAgainstTokens(
        IReadOnlyList<string> matchTokens,
        IReadOnlyList<string> titleTokens)
    {
        var score = 0;
        foreach (var token in titleTokens)
        {
            foreach (var matchToken in matchTokens)
            {
                if (string.Equals(token, matchToken, StringComparison.Ordinal))
                {
                    score++;
                    break; // count each titleToken at most once per entry
                }
            }
        }
        return score;
    }

    [Description("Look up a pinball machine by its title (case-insensitive). Returns the manufacturer, year, themes, designers, editions, OPDB source URL, and — when the machine belongs to a multi-edition group — sibling base-machine records sharing the same OPDB group so the agent can ask a targeted clarifying question for version-dependent topics. The response may also include TitleCollisions: machines from OTHER manufacturers (different OPDB groups) that share the same franchise title (e.g. Sega Godzilla 1998 alongside Stern Godzilla 2021). For questions whose answer depends on which machine is meant — year, price, rules, repair — if TitleCollisions is non-empty and the user did not specify a manufacturer, ask one targeted clarifying question naming the options (manufacturer + year) before answering. Returns null if no machine matches the title.")]
    public async Task<MachineGroundingDto?> GetMachineByTitleAsync(
        [Description("The pinball-machine title to look up, case-insensitive. Include the manufacturer name if the user stated it (for example: 'Stern Godzilla', 'Foo Fighters', 'Attack from Mars Remake'). The manufacturer qualifier resolves ambiguity when multiple machines share the same franchise title (e.g. Sega vs Stern Godzilla). On an initial lookup, omit edition suffixes like Pro/Premium/LE — those are surfaced via the returned Siblings list. When re-calling to resolve a specific edition named by the user, include the edition qualifier (e.g. 'Godzilla Premium', 'Attack from Mars Remake').")] string title,
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

            // Prefix-strip retry: the agent's [Description] instructs the LLM to
            // include both the manufacturer AND edition qualifier when re-calling for
            // a specific edition (e.g. "Stern Godzilla Premium"). However, the sync
            // service writes rows keyed on bare titles ("godzilla premium") and on
            // common manufacturer-prefixed titles ("stern godzilla", "godzilla") —
            // it does NOT write "{mfr} {title} {edition}" rows because the
            // combinatorial write amplification would be large (N mfr × M editions).
            // So "stern godzilla premium" is a 404 on the first point-read.
            //
            // Recovery: strip the leading whitespace-delimited token and retry, up to
            // 2 times, stopping on the first hit. The remainder must have ≥ 2 tokens
            // after stripping so we avoid degenerate single-word collisions
            // (e.g. "godzilla premium" → "premium" must NOT be tried).
            // Each retry is one ~5ms point-read, at most 2, only on misses.
            //
            // Scoring after a retry hit uses tokens from the ORIGINAL full title so
            // "stern" still scores manufacturer matchTokens correctly — the stripped
            // key is only the lookup address, not the scoring input.
            if (lookup is null || lookup.OpdbIds.Count == 0)
            {
                // A non-null row with empty OpdbIds is a miss for retry purposes —
                // null it so the loop condition fires for it too.
                lookup = null;

                var normalizedTokens = MachineTitleLookup.NormalizeTitle(title)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                for (var strip = 1; strip <= 2 && lookup is null; strip++)
                {
                    var remaining = normalizedTokens.Length - strip;
                    if (remaining < 2)
                        break;

                    var retryKey = string.Join(" ", normalizedTokens, strip, remaining);
                    lookup = await _titleLookups.GetByTitleAsync(retryKey, cancellationToken).ConfigureAwait(false);
                    if (lookup is not null && lookup.OpdbIds.Count > 0)
                    {
                        _logger.LogDebug(
                            "MachineGroundingTool: prefix-strip retry hit on '{RetryKey}' (original: '{OriginalTitle}', strips={Strip}).",
                            retryKey, title, strip);
                    }
                    else
                    {
                        lookup = null; // treat empty OpdbIds as a miss so the loop continues
                    }
                }
            }

            Machine? match = null;
            var lookupHit = lookup is not null && lookup.OpdbIds.Count > 0;
            // Track which lookup row resolved the match so TitleCollisions can
            // read the other entries in that row after the primary is resolved.
            // Only the lookup-row path populates TitleCollisions; the cross-
            // partition fallback path cannot — it has no row to inspect.
            MachineTitleLookup? resolvedLookup = null;
            int resolvedLookupBestIdx = 0;

            // Guard: OpdbIds, Manufacturers, and (when present) MatchTokens must all
            // be the same length (maintained by UpsertEntry / RemoveEntry). A mismatch
            // indicates data corruption (direct Cosmos edit, buggy migration, or partial
            // write). Degrade to the cross-partition fallback so the user query still
            // resolves; the next OPDB sync will rewrite the row correctly.
            if (lookupHit)
            {
                var matchTokensLengthOk = lookup!.MatchTokens is null
                    || lookup.MatchTokens.Count == lookup.OpdbIds.Count;

                if (lookup.OpdbIds.Count != lookup.Manufacturers.Count || !matchTokensLengthOk)
                {
                    _logger.LogWarning(
                        "MachineGroundingTool: lookup row for '{Title}' has mismatched array lengths — OpdbIds={OpdbCount}, Manufacturers={ManufacturerCount}, MatchTokens={MatchTokensCount}. Possible data corruption. Falling back to cross-partition query. Re-run OPDB sync to remediate.",
                        title,
                        lookup.OpdbIds.Count,
                        lookup.Manufacturers.Count,
                        lookup.MatchTokens?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");
                    lookupHit = false;
                }
            }

            if (lookupHit)
            {
                // Score every collision-row entry against tokens extracted from
                // the input title. MatchTokens (e.g. ["jjp", "jersey", "jack"])
                // are used when available so expanded display names ("Jersey Jack
                // Pirates") resolve correctly. Null fallback uses the raw
                // manufacturer key as a single-element list — backward-compatible
                // for rows written before MatchTokens was introduced.
                // The highest-scoring entry is resolved first; ties (all-zero or
                // equal scores) preserve insertion order — backward-compatible with
                // the pre-scoring first-hit behaviour for bare franchise titles.
                var titleTokens = TokenizeForOverlap(title);
                var bestIdx = 0;
                var bestScore = ScoreEntryAgainstTokens(
                    lookup!.MatchTokens?[0] ?? [lookup.Manufacturers[0]],
                    titleTokens);

                for (var i = 1; i < lookup.OpdbIds.Count; i++)
                {
                    var score = ScoreEntryAgainstTokens(
                        lookup.MatchTokens?[i] ?? [lookup.Manufacturers[i]],
                        titleTokens);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }

                var opdbId = lookup.OpdbIds[bestIdx];
                var manufacturer = lookup.Manufacturers[bestIdx];
                match = await _machines.GetByOpdbIdAsync(opdbId, manufacturer, cancellationToken).ConfigureAwait(false);
                if (match is not null)
                {
                    resolvedLookup = lookup;
                    resolvedLookupBestIdx = bestIdx;
                }

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

            // Cross-group collision resolution (ADR-0029 follow-up 2026-06-10):
            // when the lookup row has multiple entries beyond the resolved match,
            // those are machines from DIFFERENT OPDB groups sharing the same
            // franchise title. Surface them so the agent can ask one targeted
            // clarifying question (manufacturer + year) for version-dependent
            // questions. Only populated via the lookup-row path.
            var titleCollisions = await ResolveTitleCollisionsAsync(
                match, resolvedLookup, resolvedLookupBestIdx, siblings, cancellationToken).ConfigureAwait(false);

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
                Siblings: siblings,
                TitleCollisions: titleCollisions);
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
                    Editions: ProjectEditions(sibling.Editions),
                    // EditionLabel + EditionTokens (Task 7, AB#259) let the
                    // Wizard name a sibling's edition and match a user-named
                    // edition to the right base for R2/R3 reasoning.
                    EditionLabel: sibling.EditionLabel,
                    EditionTokens: sibling.EditionTokens.AsReadOnly()));
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

    // Fetches cross-group machines that share the same franchise title as
    // the resolved primary — the other entries in the lookup row that were
    // NOT scored as the best match and belong to a DIFFERENT OPDB group.
    //
    // Exclusion rules (both must hold for an entry to be skipped):
    //   • The entry IS the resolved match (same opdbId / same bestIdx).
    //   • The entry's machine shares the primary's GroupId — it is already
    //     reachable via Siblings (same-group); adding it to TitleCollisions
    //     would duplicate information the agent already has.
    //
    // Failures fetching a collision machine are silently skipped (logged
    // at Debug) so a stale/missing machine row does not poison the list or
    // abort the primary result. TitleCollisions is best-effort.
    private async Task<IReadOnlyList<MachineSiblingGroundingDto>> ResolveTitleCollisionsAsync(
        Machine primary,
        MachineTitleLookup? resolvedLookup,
        int bestIdx,
        IReadOnlyList<MachineSiblingGroundingDto> siblings,
        CancellationToken cancellationToken)
    {
        // No lookup row means we came in via the cross-partition fallback.
        // TitleCollisions is not available without a row.
        if (resolvedLookup is null || resolvedLookup.OpdbIds.Count <= 1)
            return [];

        // Build a fast exclusion set: GroupIds already covered by Siblings.
        // Any collision whose fetched machine shares one of these GroupIds
        // is already visible to the agent via Siblings.
        var siblingGroupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in siblings)
        {
            // Siblings share the primary's GroupId by construction (same OPDB group).
            // Capture the primary GroupId once; all siblings implicitly share it.
        }
        if (!string.IsNullOrEmpty(primary.GroupId))
            siblingGroupIds.Add(primary.GroupId);

        var collisions = new List<MachineSiblingGroundingDto>();
        for (var i = 0; i < resolvedLookup.OpdbIds.Count; i++)
        {
            if (i == bestIdx)
                continue; // this is the resolved match itself

            var opdbId = resolvedLookup.OpdbIds[i];
            var manufacturer = resolvedLookup.Manufacturers[i];

            try
            {
                var candidate = await _machines.GetByOpdbIdAsync(opdbId, manufacturer, cancellationToken).ConfigureAwait(false);
                if (candidate is null)
                {
                    _logger.LogDebug(
                        "MachineGroundingTool: TitleCollisions candidate opdb_id '{OpdbId}' / manufacturer '{Manufacturer}' not found — skipping.",
                        opdbId, manufacturer);
                    continue;
                }

                // Skip if the candidate is in the same OPDB group as the primary
                // (already visible via Siblings — no need to duplicate).
                if (!string.IsNullOrEmpty(candidate.GroupId) && siblingGroupIds.Contains(candidate.GroupId))
                    continue;

                collisions.Add(new MachineSiblingGroundingDto(
                    OpdbId: candidate.Id,
                    Title: candidate.Title,
                    Year: candidate.Year,
                    Editions: ProjectEditions(candidate.Editions),
                    EditionLabel: candidate.EditionLabel,
                    EditionTokens: candidate.EditionTokens.AsReadOnly()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "MachineGroundingTool: TitleCollisions fetch failed for opdb_id '{OpdbId}' / manufacturer '{Manufacturer}' — skipping.",
                    opdbId, manufacturer);
            }
        }

        return collisions;
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