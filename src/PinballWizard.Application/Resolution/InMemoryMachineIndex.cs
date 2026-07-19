using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Resolution;

// Batch-side index over machine variants. Built once per run by streaming the catalog.
// Interactive consumers use machine_title_lookups + AI Search instead — but BOTH are fed
// by MachineIdentityVariants, so they cannot diverge (ADR-0054).
public sealed class InMemoryMachineIndex
{
    private readonly Dictionary<string, List<MachineVariant>> _byKey;
    private readonly List<MachineVariant> _all;

    private InMemoryMachineIndex(Dictionary<string, List<MachineVariant>> byKey, List<MachineVariant> all)
    {
        _byKey = byKey;
        _all = all;
    }

    public int VariantCount => _all.Count;

    public static InMemoryMachineIndex Build(IEnumerable<Machine> machines, IReadOnlyList<MachineAliasEntry> aliases)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(aliases);

        var byKey = new Dictionary<string, List<MachineVariant>>(StringComparer.Ordinal);
        var all = new List<MachineVariant>();

        foreach (var m in machines)
        {
            foreach (var v in MachineIdentityVariants.For(m, aliases))
            {
                all.Add(v);
                if (!byKey.TryGetValue(v.Key, out var list))
                {
                    list = [];
                    byKey[v.Key] = list;
                }
                list.Add(v);
            }
        }

        // Longest variant first so containment matching prefers "galactic tank force" over "tank".
        all.Sort((a, b) => b.Tokens.Count.CompareTo(a.Tokens.Count));
        return new InMemoryMachineIndex(byKey, all);
    }

    public IReadOnlyList<MachineVariant> Exact(string key) =>
        _byKey.TryGetValue(key, out var list) ? list : [];

    // Ordered longest-first. Callers stop at the first token-count tier that yields a match.
    public IReadOnlyList<MachineVariant> AllLongestFirst() => _all;
}
