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
    IReadOnlyList<TiltForumsMachineMatch> Machines);

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
    public static async Task<TiltForumsGameMatchResult> ResolveAsync(
        IMachineRepository machineRepository,
        string gameTitle,
        string manufacturerHeaderText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machineRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturerHeaderText);

        var manufacturerKey = OpdbMachineMapper.NormalizeManufacturerKey(manufacturerHeaderText);

        var matches = new List<Machine>();
        await foreach (var machine in machineRepository.QueryByTitleAsync(gameTitle, cancellationToken))
        {
            if (string.Equals(machine.PartitionKey, manufacturerKey, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(machine);
            }
        }

        if (matches.Count == 0)
        {
            return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, []);
        }

        if (matches.Count == 1)
        {
            return new TiltForumsGameMatchResult(
                TiltForumsGameMatchStatus.Resolved,
                [ToMatch(matches[0])]);
        }

        if (EditionFamily.IsEditionFamily(matches))
        {
            var siblings = await CollectSiblingsAsync(machineRepository, matches[0].GroupId!, cancellationToken);
            return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.ResolvedEditionFamily, siblings);
        }

        return new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, []);
    }

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

    private static TiltForumsMachineMatch ToMatch(Machine machine) =>
        new(machine.Id, machine.Title, machine.ManufacturerDisplayName);
}
