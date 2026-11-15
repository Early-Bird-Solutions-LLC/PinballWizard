using PinballWizard.Infrastructure.Integrations.Opdb;

namespace PinballWizard.Infrastructure.Integrations.PinTips;

// A catalog machine a tip can be indexed under. MachineId is the full OPDB
// id (GweeP-MW95j), which is what searchCorpus filters on.
public sealed record PinTipsIndexTarget(string MachineId, string MachineTitle, string Manufacturer);

public static class PinTipsCatalogTargets
{
    // PinTips rows usually carry the OPDB group id (G5pe4), not a machine id.
    // A group id fans out to every machine in that group. A two-segment id
    // is one machine. A three-segment alias id matches that machine's base
    // id (the first two segments). An empty result is not indexed.
    public static IReadOnlyList<PinTipsIndexTarget> Resolve(
        string tipOpdbId,
        IReadOnlyList<PinTipsIndexTarget> machinesInGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tipOpdbId);
        ArgumentNullException.ThrowIfNull(machinesInGroup);

        if (!tipOpdbId.Contains('-', StringComparison.Ordinal))
        {
            return machinesInGroup;
        }

        var exact = machinesInGroup
            .Where(m => string.Equals(m.MachineId, tipOpdbId, StringComparison.Ordinal))
            .ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        // Three-segment alias ids (Gxxx-Myyy-Azzz) are not catalog machine
        // ids. The base machine is the first two segments.
        var baseId = OpdbMachineMapper.GetBaseMachineOpdbId(tipOpdbId);
        if (baseId is null)
        {
            return [];
        }

        return machinesInGroup
            .Where(m => string.Equals(m.MachineId, baseId, StringComparison.Ordinal))
            .ToList();
    }
}
