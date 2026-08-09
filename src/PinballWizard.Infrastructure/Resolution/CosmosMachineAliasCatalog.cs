using PinballWizard.Application.Persistence;
using PinballWizard.Application.Resolution;

namespace PinballWizard.Infrastructure.Resolution;

// Binds IMachineAliasCatalog to the machine repository so MachineAliasLoader can
// fail closed on an alias pointing at a machine or group that does not exist.
// Streams once and caches: the loader validates every seed entry at startup, and a
// per-entry cross-partition query would be one Cosmos round-trip per alias.
public sealed class CosmosMachineAliasCatalog : IMachineAliasCatalog
{
    private readonly IMachineRepository _machineRepo;
    private volatile Dictionary<string, string>? _machineToMfr;
    private Dictionary<string, HashSet<string>>? _groupToMfrs;

    public CosmosMachineAliasCatalog(IMachineRepository machineRepo)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        _machineRepo = machineRepo;
    }

    public async Task<bool> MachineExistsAsync(string machineId, string manufacturerKey, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _machineToMfr!.TryGetValue(machineId, out var mfr)
            && string.Equals(mfr, manufacturerKey, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> GroupExistsAsync(string groupId, string manufacturerKey, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _groupToMfrs!.TryGetValue(groupId, out var mfrs) && mfrs.Contains(manufacturerKey);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_machineToMfr is not null) return;

        var machines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await foreach (var m in _machineRepo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            machines[m.Id] = m.PartitionKey;
            if (m.GroupId is { Length: > 0 } g)
            {
                if (!groups.TryGetValue(g, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    groups[g] = set;
                }
                set.Add(m.PartitionKey);
            }
        }

        _groupToMfrs = groups;
        _machineToMfr = machines;   // assigned LAST and volatile: the volatile write is a
                                    // release fence — any thread that subsequently reads
                                    // _machineToMfr != null (an acquire read, because the
                                    // field is volatile) is guaranteed to also see the
                                    // completed _groupToMfrs write above. Do not remove
                                    // volatile from _machineToMfr and do not reorder these
                                    // two assignments; either change breaks the invariant on
                                    // ARM and other weakly-ordered architectures.
    }
}
