using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Integrations.Opdb;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// Outcome of resolving a Tilt Forums rulesheet's game title to a single
/// catalog <c>Machine</c>, scoped to the manufacturer the master list
/// grouped it under.
/// </summary>
public enum TiltForumsGameMatchStatus
{
    /// <summary>Exactly one machine matched the title within the resolved manufacturer partition.</summary>
    Resolved,

    /// <summary>No machine matched the title within the resolved manufacturer partition.</summary>
    NoMatchInManufacturerPartition,

    /// <summary>More than one machine matched the title within the same manufacturer partition — a genuine same-manufacturer edition collision. Not guessed.</summary>
    MultipleMatchesInManufacturerPartition,
}

/// <summary>Result of <see cref="TiltForumsGameMatcher.ResolveAsync"/>.</summary>
public sealed record TiltForumsGameMatchResult(
    TiltForumsGameMatchStatus Status,
    string? MachineId,
    string? MachineTitle,
    string? ManufacturerDisplayName);

/// <summary>
/// Resolves a Tilt Forums rulesheet's (title, manufacturer header text) pair
/// to a single catalog <c>Machine</c>.
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
/// the match — never falling back to an unscoped guess.
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

        var matches = new List<PinballWizard.Core.Domain.Machine>();
        await foreach (var machine in machineRepository.QueryByTitleAsync(gameTitle, cancellationToken))
        {
            if (string.Equals(machine.PartitionKey, manufacturerKey, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(machine);
            }
        }

        return matches.Count switch
        {
            0 => new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, null, null, null),
            1 => new TiltForumsGameMatchResult(
                TiltForumsGameMatchStatus.Resolved, matches[0].Id, matches[0].Title, matches[0].ManufacturerDisplayName),
            _ => new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, null, null, null),
        };
    }
}
