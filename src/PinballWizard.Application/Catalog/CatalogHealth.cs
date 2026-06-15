namespace PinballWizard.Application.Catalog;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "\"Flag\" is accurate domain language for a catalog-health diagnostic signal; this is not a [Flags] bitmask enum.")]
public enum CatalogHealthFlag { Ok, Empty, NoManual, EditionGap }

public static class CatalogHealth
{
    // Pure: flags for one machine given its same-GroupId siblings.
    public static IReadOnlyList<CatalogHealthFlag> Evaluate(
        MachineDocStats machine, IReadOnlyList<MachineDocStats> siblings)
    {
        var flags = new List<CatalogHealthFlag>();
        if (machine.DocCount == 0) flags.Add(CatalogHealthFlag.Empty);
        else if (!machine.HasManual) flags.Add(CatalogHealthFlag.NoManual);

        // Edition gap: a same-GroupId sibling has strictly more docs.
        if (machine.GroupId is not null &&
            siblings.Any(s => s.GroupId == machine.GroupId && s.DocCount > machine.DocCount))
            flags.Add(CatalogHealthFlag.EditionGap);

        return flags.Count == 0 ? [CatalogHealthFlag.Ok] : flags;
    }
}
