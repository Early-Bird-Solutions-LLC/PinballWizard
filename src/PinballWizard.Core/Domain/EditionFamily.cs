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

    /// <summary>
    /// True when every candidate shares a single non-null <see cref="Machine.GroupId"/>,
    /// with no year requirement — a franchise whose OPDB editions span multiple release
    /// years under one GroupId (an original release plus a later Vault Edition/reissue,
    /// e.g. AC/DC 2012 vs. 2017) is still one edition family. Per issue #677: the year
    /// guard in <see cref="IsEditionFamily"/> was meant to separate genuine reissues from
    /// an unrelated game that happens to reuse a group segment, but that can't happen —
    /// GroupId is an OPDB-assigned relational key, not a coincidental string — so the
    /// guard only blocked <see cref="EditionResolver"/> from ever running against
    /// cross-year families. Mirrors the reconciler's own GroupId-only check
    /// (<c>ScraperReconciliationService.IsEditionFamilyByGroup</c>, added for issue #655
    /// Gap 1) so document-linking and machine-reconciliation agree on what a family is.
    /// </summary>
    public static bool IsEditionFamilyByGroup(IReadOnlyList<Machine> candidates)
    {
        if (candidates.Count == 0) return false;
        var groupIds = candidates.Select(m => m.GroupId).Distinct().ToList();
        return groupIds.Count == 1 && groupIds[0] is not null;
    }
}
