namespace PinballWizard.Infrastructure.Integrations.PinTips;

// A catalog machine a tip can be indexed under. MachineId is the full OPDB
// id (GweeP-MW95j), which is what searchCorpus filters on.
public sealed record PinTipsIndexTarget(string MachineId, string MachineTitle, string Manufacturer);

public static class PinTipsCatalogTargets
{
    // PinTips rows usually carry the OPDB group id (G5pe4), not a machine id.
    // A group id fans out to every machine in that group. A hyphenated id is
    // one machine and is kept only when that machine is in the list.
    // An empty result means the tip must not be indexed: a group id stored
    // as machine_id never matches a machine-scoped search.
    public static IReadOnlyList<PinTipsIndexTarget> Resolve(
        string tipOpdbId,
        IReadOnlyList<PinTipsIndexTarget> machinesInGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tipOpdbId);
        ArgumentNullException.ThrowIfNull(machinesInGroup);

        if (tipOpdbId.Contains('-', StringComparison.Ordinal))
        {
            return machinesInGroup
                .Where(m => string.Equals(m.MachineId, tipOpdbId, StringComparison.Ordinal))
                .ToList();
        }

        return machinesInGroup;
    }
}
