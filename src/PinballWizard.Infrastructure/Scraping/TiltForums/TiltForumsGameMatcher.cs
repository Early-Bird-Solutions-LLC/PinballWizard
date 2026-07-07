using Microsoft.Extensions.Logging;
using PinballWizard.Application.Findability;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Integrations.Opdb;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// Outcome of resolving a Tilt Forums rulesheet's game title to catalog
/// <c>Machine</c>(s), scoped to the manufacturer the master list grouped it
/// under.
/// </summary>
public enum TiltForumsGameMatchStatus
{
    /// <summary>Exactly one machine matched the title within the resolved manufacturer partition.</summary>
    Resolved,

    /// <summary>Multiple machines matched, all in the same edition family (same GroupId+Year) — fanned out to every sibling via GetSiblingsByGroupIdAsync.</summary>
    ResolvedEditionFamily,

    /// <summary>No machine matched the title within the resolved manufacturer partition.</summary>
    NoMatchInManufacturerPartition,

    /// <summary>Multiple machines matched, NOT an edition family (different GroupIds/Years, or missing GroupId/Year data) — a genuine cross-game title collision. Not guessed.</summary>
    MultipleMatchesInManufacturerPartition,
}

/// <summary>One machine target a resolved rulesheet should be indexed against.</summary>
public sealed record TiltForumsMachineMatch(string MachineId, string MachineTitle, string ManufacturerDisplayName);

/// <summary>
/// Result of <see cref="TiltForumsGameMatcher.ResolveAsync"/>. <see cref="Machines"/> is empty for
/// <see cref="TiltForumsGameMatchStatus.NoMatchInManufacturerPartition"/> and
/// <see cref="TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition"/>, has exactly one
/// entry for <see cref="TiltForumsGameMatchStatus.Resolved"/>, and one entry per sibling edition
/// for <see cref="TiltForumsGameMatchStatus.ResolvedEditionFamily"/>.
/// </summary>
public sealed record TiltForumsGameMatchResult(
    TiltForumsGameMatchStatus Status,
    IReadOnlyList<TiltForumsMachineMatch> Machines,
    bool ResolvedViaFuzzy = false);

/// <summary>
/// Resolves a Tilt Forums rulesheet's (title, manufacturer header text) pair
/// to one or more catalog <c>Machine</c>s.
/// </summary>
/// <remarks>
/// Every existing single-manufacturer scraper's HTTP client only ever
/// touches one manufacturer's site, so nothing in the codebase before this
/// has had to disambiguate a title across manufacturer partitions at
/// scrape/sync time — <c>IMachineTitleLookupRepository</c>'s own fallback
/// path takes the first OPDB id unscoped (see
/// <c>KineticistTutorialsClient</c>'s "legacy fallback" comment). Tilt
/// Forums is genuinely cross-manufacturer, so this type exists specifically
/// to avoid that class of silent wrong-manufacturer match: it uses the
/// manufacturer hint the master list's own section headers already provide,
/// normalized via the existing <see cref="OpdbMachineMapper.NormalizeManufacturerKey"/>,
/// to filter <see cref="IMachineRepository.QueryByTitleAsync"/>'s
/// cross-partition results down to the one partition that should contain
/// the match. A multi-match within that partition is fanned out to every
/// sibling edition (per ADR-0032, rulesheets are franchise-wide documents)
/// ONLY when <see cref="EditionFamily.IsEditionFamily"/> proves the
/// candidates are the same base game — never falling back to an unscoped
/// guess for a genuine cross-game collision.
/// </remarks>
public static class TiltForumsGameMatcher
{
    // Top hits requested from the machine index. 5 gives enough headroom to see
    // a same-title different-group collision while bounding the query.
    private const int MachineIndexTopHits = 5;

    public static async Task<TiltForumsGameMatchResult> ResolveAsync(
        IMachineRepository machineRepository,
        IMachineSearchIndex? machineSearchIndex,
        string gameTitle,
        string? manufacturerHeaderText,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(machineRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);

        // null/whitespace manufacturer hint = subcategory topic → unscoped resolution.
        var manufacturerKey = string.IsNullOrWhiteSpace(manufacturerHeaderText)
            ? null
            : OpdbMachineMapper.NormalizeManufacturerKey(manufacturerHeaderText);

        var matches = new List<Machine>();
        await foreach (var machine in machineRepository.QueryByTitleAsync(gameTitle, cancellationToken))
        {
            // Scoped: keep only the hinted partition. Unscoped (key null): keep all.
            if (manufacturerKey is null
                || string.Equals(machine.PartitionKey, manufacturerKey, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(machine);
            }
        }

        if (matches.Count == 1)
        {
            return new TiltForumsGameMatchResult(
                TiltForumsGameMatchStatus.Resolved,
                [ToMatch(matches[0])]);
        }

        if (matches.Count > 1)
        {
            if (EditionFamily.IsEditionFamily(matches))
            {
                var siblings = await CollectSiblingsAsync(machineRepository, matches[0].GroupId!, cancellationToken);
                return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.ResolvedEditionFamily, siblings);
            }

            return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, []);
        }

        // matches.Count == 0 — exact miss. Try the forgiving machine-index path,
        // scoped to this manufacturer partition. Absent index (AI Search
        // unconfigured / null-object empty) degrades to the historical NoMatch.
        if (machineSearchIndex is not null)
        {
            var fuzzy = await ResolveViaMachineIndexAsync(
                machineRepository, machineSearchIndex, gameTitle, manufacturerKey, cancellationToken, logger);
            if (fuzzy is not null)
                return fuzzy;
        }

        return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, []);
    }

    // Forgiving fallback: resolve gameTitle via the machine findability index,
    // scoped to manufacturerKey (null = unscoped cross-partition search).
    // Returns null when the index yields nothing usable so the caller emits the historical NoMatch.
    private static async Task<TiltForumsGameMatchResult?> ResolveViaMachineIndexAsync(
        IMachineRepository machineRepository,
        IMachineSearchIndex machineSearchIndex,
        string gameTitle,
        string? manufacturerKey,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        IReadOnlyList<MachineSearchHit> hits;
        try
        {
            hits = await machineSearchIndex.SearchAsync(
                gameTitle, MachineIndexTopHits, manufacturerKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "TiltForumsGameMatcher: machine index query for '{GameTitle}' (manufacturer '{ManufacturerKey}') failed — degrading to no-match.",
                gameTitle, manufacturerKey);
            PinballWizardTelemetry.MachineSearchErrors.Add(
                1, new KeyValuePair<string, object?>("reason", "tiltforums_fuzzy_unavailable"));
            return null;
        }

        if (hits.Count == 0)
            return null;

        var topHit = hits[0];

        // Point-read the authoritative Machine for the top hit. A stale index row
        // (hit present, machine gone) degrades to NoMatch (invariant #17).
        var topMachine = await machineRepository.GetByOpdbIdAsync(
            topHit.OpdbId, topHit.ManufacturerKey, cancellationToken);
        if (topMachine is null)
        {
            logger?.LogWarning(
                "TiltForumsGameMatcher: stale index — hit '{OpdbId}' present in AI Search but machine row absent from Cosmos for manufacturer '{ManufacturerKey}'. Degrading to no-match; will self-heal on next machine-index projection.",
                topHit.OpdbId, topHit.ManufacturerKey);
            return null;
        }

        // Cross-group same-title collision guard: a different-group hit that carries
        // the SAME title as the top hit is a genuine same-name-different-game
        // ambiguity — do not guess.
        var topGroupKey = GroupKeyOf(topHit.OpdbId, topHit.GroupId);
        foreach (var other in hits.Skip(1))
        {
            if (!string.Equals(GroupKeyOf(other.OpdbId, other.GroupId), topGroupKey, StringComparison.Ordinal)
                && string.Equals(other.Title, topHit.Title, StringComparison.OrdinalIgnoreCase))
            {
                return new TiltForumsGameMatchResult(
                    TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, []);
            }
        }

        // Resolve the top machine's edition family. A clean same-group+year family
        // fans out to every sibling (ADR-0032); a mixed-year/incomplete group grounds
        // the top machine alone rather than fanning onto a different-year game.
        if (!string.IsNullOrEmpty(topMachine.GroupId))
        {
            var siblings = await CollectMachinesAsync(
                machineRepository.GetSiblingsByGroupIdAsync(topMachine.GroupId, cancellationToken));
            if (siblings.Count > 1 && EditionFamily.IsEditionFamily(siblings))
            {
                return new TiltForumsGameMatchResult(
                    TiltForumsGameMatchStatus.ResolvedEditionFamily,
                    siblings.Select(ToMatch).ToList(),
                    ResolvedViaFuzzy: true);
            }
        }

        return new TiltForumsGameMatchResult(
            TiltForumsGameMatchStatus.Resolved, [ToMatch(topMachine)], ResolvedViaFuzzy: true);
    }

    private static string GroupKeyOf(string opdbId, string? groupId) =>
        string.IsNullOrEmpty(groupId) ? opdbId : groupId;

    // Fetches the COMPLETE sibling set from the repository rather than
    // trusting the title-matched candidates already in hand — a sibling
    // edition can carry different exact title text (e.g. a "Collector's
    // Edition" variant), which QueryByTitleAsync would never have surfaced.
    // Matches the same primitive --sync-kineticist-tutorials already uses.
    private static async Task<IReadOnlyList<TiltForumsMachineMatch>> CollectSiblingsAsync(
        IMachineRepository machineRepository, string groupId, CancellationToken cancellationToken)
    {
        var siblings = new List<TiltForumsMachineMatch>();
        await foreach (var machine in machineRepository.GetSiblingsByGroupIdAsync(groupId, cancellationToken))
        {
            siblings.Add(ToMatch(machine));
        }
        return siblings;
    }

    private static async Task<List<Machine>> CollectMachinesAsync(IAsyncEnumerable<Machine> source)
    {
        var list = new List<Machine>();
        await foreach (var m in source)
            list.Add(m);
        return list;
    }

    private static TiltForumsMachineMatch ToMatch(Machine machine) =>
        new(machine.Id, machine.Title, machine.ManufacturerDisplayName);
}
