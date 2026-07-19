using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Resolution;

// Derives every matchable variant of a machine from its CANONICAL catalog identity.
// This is the heart of ADR-0054: identity (title/manufacturer/group) is the join key;
// ManufacturerSlugs is demoted to one evidence source among several.
// The same generator feeds both the batch index and machine_title_lookups, so the
// two stores cannot diverge again.
public static class MachineIdentityVariants
{
    // Copied verbatim from ScraperReconciliationService.DecorationWords (ownership moves here).
    // Longest-first so compound qualifiers are consumed before their fragments.
    //
    // IMPORTANT: These entries are used in TWO ways by this class:
    //   1. As single tokens in StripTrailingQualifiers (e.g. "pinball", "remake", "edition").
    //   2. As compound qualifiers checked by joining adjacent trailing tokens (e.g. "merlinedition"
    //      matches the two-token sequence ["merlin", "edition"] in StripTrailingQualifiers).
    //
    // The reconciler's IsTrailingQualifierOnly works against a pre-concatenated string, so
    // "merlinedition" naturally matched "merlinedition" as a substring. Here we work against
    // tokenized arrays, so compound entries require the two-token join check — that is why
    // StripTrailingQualifiers checks adjacent-token compounds before single tokens (longest match
    // first, same principle as the reconciler's ordering comment).
    // internal, not private: MachineResolver initialises its lookup from this array so the
    // list exists exactly once. It previously held a second hand-maintained copy kept in
    // step by a comment — and one of these entries ("pinball") is the guard that stopped
    // the 1977 Stern machine from claiming 172 documents. A guard that depends on someone
    // remembering to edit two files is not a guard.
    internal static readonly string[] TrailingQualifiers =
    [
        "merlinedition", "vaultedition", "limitededition", "standardedition",
        "remake", "pinball", "gamekit", "deposit", "edition",
    ];

    public static IReadOnlyList<MachineVariant> For(Machine machine, IReadOnlyList<MachineAliasEntry> aliases)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(aliases);

        var mfr = machine.PartitionKey;
        var variants = new List<MachineVariant>(8);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? text, VariantKind kind)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (MachineTextNormalizer.Tokenize(text).Count == 0) return;
            var v = MachineVariant.Create(text, kind, machine.Id, mfr, machine.GroupId);
            if (seen.Add($"{v.Key}|{kind}")) variants.Add(v);
        }

        Add(machine.Title, VariantKind.FullTitle);

        var franchise = FranchiseTitle(machine.Title);
        if (!string.IsNullOrWhiteSpace(franchise)) Add(franchise, VariantKind.FranchiseTitle);

        foreach (var token in machine.EditionTokens ?? [])
            Add($"{machine.Title} {token}", VariantKind.TitleWithEdition);

        foreach (var mfrToken in ManufacturerMatchTokens.GetMatchTokens(mfr))
            Add($"{mfrToken} {machine.Title}", VariantKind.ManufacturerPrefixed);

        foreach (var slug in (machine.ManufacturerSlugs ?? []).Values)
            Add(slug, VariantKind.ScraperSlug);

        foreach (var a in aliases)
        {
            if (!string.Equals(a.ManufacturerKey, mfr, StringComparison.OrdinalIgnoreCase)) continue;
            var appliesToGroup = a.OpdbGroupId is not null
                && string.Equals(a.OpdbGroupId, machine.GroupId, StringComparison.OrdinalIgnoreCase);
            var appliesToMachine = a.MachineId is not null
                && string.Equals(a.MachineId, machine.Id, StringComparison.OrdinalIgnoreCase);
            if (appliesToGroup || appliesToMachine) Add(a.Alias, VariantKind.CuratedAlias);
        }

        return variants;
    }

    // "Houdini: Master of Mystery" → "houdini"; "Medieval Madness Merlin Edition Pinball" → "medieval madness"
    private static string FranchiseTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var head = title;
        foreach (var sep in new[] { ": ", " - " })
        {
            var i = head.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) head = head[..i];
        }

        return string.Join(' ', StripTrailingQualifiers(MachineTextNormalizer.Tokenize(head)));
    }

    // Consumes trailing qualifier tokens right-to-left. Never strips the last remaining token.
    //
    // Each iteration checks compound qualifiers (two adjacent trailing tokens joined) BEFORE
    // single-token qualifiers — longest-match-first, matching the principle in
    // ScraperReconciliationService where compound entries must precede their components.
    // This is what allows "merlin edition" (two tokens) to be consumed as the compound
    // "merlinedition" in TrailingQualifiers, which would be invisible to single-token matching.
    public static IReadOnlyList<string> StripTrailingQualifiers(IReadOnlyList<string> tokens)
    {
        var work = tokens.ToList();
        var changed = true;
        while (changed && work.Count > 1)
        {
            changed = false;

            // Compound check first: join the last two tokens and see if together they form
            // a known qualifier (e.g. "merlin"+"edition" = "merlinedition").
            // Requires at least three tokens so we never reduce below the one-token floor.
            if (work.Count > 2)
            {
                var compound = work[^2] + work[^1];
                foreach (var q in TrailingQualifiers)
                {
                    if (string.Equals(compound, q, StringComparison.Ordinal))
                    {
                        work.RemoveAt(work.Count - 1);
                        work.RemoveAt(work.Count - 1);
                        changed = true;
                        break;
                    }
                }
            }

            // Single-token check if compound did not fire.
            if (!changed)
            {
                foreach (var q in TrailingQualifiers)
                {
                    if (work.Count > 1 && string.Equals(work[^1], q, StringComparison.Ordinal))
                    {
                        work.RemoveAt(work.Count - 1);
                        changed = true;
                        break;
                    }
                }
            }
        }
        return work;
    }
}
