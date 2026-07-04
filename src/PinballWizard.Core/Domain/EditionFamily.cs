namespace PinballWizard.Core.Domain;

/// <summary>
/// Determines whether a set of catalog machines represents the same base
/// game released as multiple editions (Pro/Premium/LE) — the discriminator
/// between "fan a franchise-wide document out to every sibling" and
/// "genuinely different games that happen to share a title," per ADR-0032.
/// </summary>
public static class EditionFamily
{
    /// <summary>
    /// True when every candidate shares a single non-null <see cref="Machine.GroupId"/>
    /// AND a single non-null <see cref="Machine.Year"/>. The year guard separates
    /// genuine same-year edition siblings from an unrelated reissue/remake that
    /// happens to reuse the same group segment. A single candidate that carries
    /// a GroupId+Year also counts — used to correctly tag a lone edition's
    /// EditionScope as distinct from an ungrouped, standalone machine.
    /// </summary>
    public static bool IsEditionFamily(IReadOnlyList<Machine> candidates)
    {
        if (candidates.Count == 0) return false;
        var groupIds = candidates.Select(m => m.GroupId).Distinct().ToList();
        var years = candidates.Select(m => m.Year).Distinct().ToList();
        return groupIds.Count == 1 && groupIds[0] is not null
            && years.Count == 1 && years[0] is not null;
    }
}
