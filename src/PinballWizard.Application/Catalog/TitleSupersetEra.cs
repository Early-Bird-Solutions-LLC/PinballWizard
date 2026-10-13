using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Catalog;

// Title-superset era choice (issue #596).
//
// "Iron Maiden" (1981) and "Iron Maiden: Legacy of the Beast" (2018) share a
// franchise token. An exact-title match, or a scraper slug sitting on the
// shorter title, is not identity — the longer game's manufacturer page is
// often titled with the bare franchise. Year and edition are the signals that
// say which machine a page or a document belongs to. When those signals
// conflict or are absent, the caller must not guess: citing the other era is
// worse than leaving the document unattached (OBS-01).
public static class TitleSupersetEra
{
    public enum ChoiceKind { NotApplicable, Reject, Chosen }

    public sealed record Choice(ChoiceKind Kind, IReadOnlyList<Machine> Machines, string Signal)
    {
        public static Choice NotApplicable { get; } = new(ChoiceKind.NotApplicable, [], "");

        public static Choice Reject { get; } = new(ChoiceKind.Reject, [], "");

        public static Choice Chosen(IReadOnlyList<Machine> machines, string signal) =>
            new(ChoiceKind.Chosen, machines, signal);
    }

    public static bool IsSubtitleSuperset(string? shorter, string? longer)
    {
        if (string.IsNullOrWhiteSpace(shorter) || string.IsNullOrWhiteSpace(longer)) return false;
        var s = shorter.Trim();
        var l = longer.Trim();
        if (l.Length <= s.Length) return false;
        return l.StartsWith(s + ": ", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith(s + " - ", StringComparison.OrdinalIgnoreCase);
    }

    // True when `longer` is a different OPDB group whose title adds a subtitle
    // to `shorter`. Same-group editions are not an era collision.
    public static bool IsCrossGroupSuperset(Machine shorter, Machine longer)
    {
        ArgumentNullException.ThrowIfNull(shorter);
        ArgumentNullException.ThrowIfNull(longer);
        if (shorter.GroupId is not null && longer.GroupId is not null
            && string.Equals(shorter.GroupId, longer.GroupId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSubtitleSuperset(shorter.Title, longer.Title);
    }

    public static bool HasCrossGroupSuperset(IReadOnlyList<Machine> machines)
    {
        ArgumentNullException.ThrowIfNull(machines);
        for (var i = 0; i < machines.Count; i++)
        {
            for (var j = 0; j < machines.Count; j++)
            {
                if (i == j) continue;
                if (IsCrossGroupSuperset(machines[i], machines[j])) return true;
            }
        }

        return false;
    }

    // Another machine in the partition is a subtitle-superset of `machine`
    // (the shape that makes an exact-title slug stamp unsafe).
    public static bool HasLongerSibling(IReadOnlyList<Machine> partition, Machine machine)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(machine);
        return partition.Any(other => IsCrossGroupSuperset(machine, other));
    }

    // Reconciler: the scraped game page carried a year and/or edition names.
    // Returns every candidate in the one group those signals agree on, or null
    // when the signals are missing, tie, or contradict — null means do not guess.
    public static IReadOnlyList<Machine>? TryChooseGroup(
        IReadOnlyList<Machine> candidates,
        int? releaseYear,
        IReadOnlyCollection<string> editionNames)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(editionNames);
        if (!HasCrossGroupSuperset(candidates)) return null;

        string? yearGroup = null;
        if (releaseYear is int year)
        {
            var groups = candidates.Where(m => m.Year == year).Select(GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (groups.Count > 1) return null;
            if (groups.Count == 1) yearGroup = groups[0];
        }

        string? editionGroup = null;
        var tokens = NormalizeEditionNames(editionNames);
        if (tokens.Count > 0)
        {
            var groups = candidates
                .Where(m => HasEditionToken(m, tokens))
                .Select(GroupKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (groups.Count > 1) return null;
            if (groups.Count == 1) editionGroup = groups[0];
        }

        if (yearGroup is not null && editionGroup is not null
            && !string.Equals(yearGroup, editionGroup, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var chosen = yearGroup ?? editionGroup;
        if (chosen is null) return null;
        return candidates.Where(m => string.Equals(GroupKey(m), chosen, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // Linker: a document discovered on a shared franchise slug. An edition token
    // that only one era carries wins, including over a slug sitting on the other
    // era. With no edition token, a slug owned by the LONGER title is trusted
    // (the reconciler has already moved it); a slug owned only by the shorter
    // title is not — that is the #596 mis-stamp, and following it would cite the
    // other machine.
    public static Choice ForDocument(
        IReadOnlyList<Machine> candidates,
        string? editionToken,
        string? scraperSlug,
        string? manufacturerKey,
        bool groupLevelDocument)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!HasCrossGroupSuperset(candidates)) return Choice.NotApplicable;

        if (!string.IsNullOrEmpty(editionToken) && !groupLevelDocument)
        {
            var tokens = NormalizeEditionNames([editionToken]);
            var hits = candidates.Where(m => HasEditionToken(m, tokens)).ToList();
            var groups = hits.Select(GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (groups.Count != 1) return Choice.Reject;
            return Choice.Chosen(hits, "edition");
        }

        var longOwners = candidates
            .Where(m => IsLongSide(m, candidates) && OwnsSlug(m, scraperSlug, manufacturerKey))
            .ToList();
        var ownerGroups = longOwners.Select(GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ownerGroups.Count == 1)
        {
            var group = ownerGroups[0];
            var machines = candidates
                .Where(m => string.Equals(GroupKey(m), group, StringComparison.OrdinalIgnoreCase) && IsLongSide(m, candidates))
                .ToList();
            return Choice.Chosen(machines, "slug");
        }

        return Choice.Reject;
    }

    public static HashSet<string> NormalizeEditionNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToLowerInvariant()))
        {
            set.Add(n);
            if (n is "limited edition" or "limited") set.Add("le");
            if (n is "prem") set.Add("premium");
        }

        return set;
    }

    private static bool HasEditionToken(Machine machine, HashSet<string> tokens) =>
        machine.EditionTokens.Any(t => tokens.Contains(t));

    private static bool IsLongSide(Machine machine, IReadOnlyList<Machine> candidates) =>
        candidates.Any(other => IsCrossGroupSuperset(other, machine));

    private static bool OwnsSlug(Machine machine, string? slug, string? manufacturerKey)
    {
        if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(manufacturerKey)) return false;
        return machine.ManufacturerSlugs.TryGetValue(manufacturerKey, out var owned)
            && string.Equals(owned, slug, StringComparison.OrdinalIgnoreCase);
    }

    private static string GroupKey(Machine machine) =>
        string.IsNullOrEmpty(machine.GroupId) ? machine.Id : machine.GroupId;
}
