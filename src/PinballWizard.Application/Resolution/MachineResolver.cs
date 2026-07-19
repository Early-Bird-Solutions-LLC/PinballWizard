using PinballWizard.Application.Observability;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Resolution;

// Evidence-aware, confidence-tiered resolution (ADR-0054).
// It NEVER guesses: multiple non-family candidates yield Ambiguous, which the caller
// turns into needs_review. A wrongly-attributed document is worse than an unattributed one.
public sealed class MachineResolver : IMachineResolver
{
    private readonly InMemoryMachineIndex _index;
    private readonly IReadOnlyDictionary<string, Machine> _machines;

    // Initialised FROM MachineIdentityVariants.TrailingQualifiers — one list, not two kept
    // in step by convention. Trailing-qualifier single-token variants are the root cause of
    // the 172-document "Pinball" incident: the 1977 Stern machine titled "Pinball" matched
    // any document whose filename contained the word "pinball". Stage 3 guards against it,
    // so a divergence between the two lists would silently reopen that class.
    private static readonly HashSet<string> TrailingQualifiers =
        new(MachineIdentityVariants.TrailingQualifiers, StringComparer.Ordinal);

    public MachineResolver(InMemoryMachineIndex index, IReadOnlyDictionary<string, Machine> machines)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(machines);
        _index = index;
        _machines = machines;
    }

    // Fuzzy evidence may mention any machine, so manufacturer scoping is a HARD filter.
    // Provenance evidence is the scraper's own claim, so scoping is a soft preference
    // (preserves DocumentLinker's deliberate NarrowToSourceManufacturer vs PreferByManufacturer split).
    private static bool IsFuzzy(EvidenceKind k) => k is EvidenceKind.Filename or EvidenceKind.PageText;

    // Meters at the single exit rather than at each of ResolveCore's five return
    // points — one place to keep correct, and no return path can escape the counter.
    // A full relink runs this tens of thousands of times unattended, so the outcome
    // mix is the only signal that resolution policy still behaves.
    public ResolutionResult Resolve(ResolutionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var result = ResolveCore(query);

        var (outcome, stage) = result switch
        {
            ResolutionResult.Resolved r => ("resolved", r.Evidence.Stage.ToString()),
            ResolutionResult.ResolvedFamily f => ("resolved_family", f.Evidence.Stage.ToString()),
            ResolutionResult.Ambiguous a => ("ambiguous", a.Evidence.Stage.ToString()),
            _ => ("no_match", "none"),
        };

        PinballWizardTelemetry.MachineResolutionTotal.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("evidence_kind", query.EvidenceKind.ToString()));

        return result;
    }

    private ResolutionResult ResolveCore(ResolutionQuery query)
    {
        var tokens = MachineTextNormalizer.Tokenize(query.Text);
        if (tokens.Count == 0) return new ResolutionResult.NoMatch();
        var key = string.Join(' ', tokens);

        // Stage 1 — exact. All variants are eligible here, including single-token variants.
        var exact = Eligible(_index.Exact(key), query, ResolutionStage.Exact);
        if (exact.Count > 0) return Decide(exact, query, ResolutionStage.Exact);

        // Stage 2 — franchise prefix + trailing-qualifier consumption (generalizes PR #750).
        // Strips structural decoration from the right of the query (e.g., "pinball", "edition")
        // and checks if the remainder exactly matches a variant. Only fires when stripping
        // actually reduces the token count.
        var stripped = MachineIdentityVariants.StripTrailingQualifiers(tokens);
        if (stripped.Count != tokens.Count)
        {
            var prefix = Eligible(_index.Exact(string.Join(' ', stripped)), query, ResolutionStage.FranchisePrefix);
            if (prefix.Count > 0) return Decide(prefix, query, ResolutionStage.FranchisePrefix);
        }

        // Stage 3 — token word-boundary containment, longest variant wins.
        // Iterates variants longest-first; once a match is found at length N, all
        // shorter variants are skipped — this ensures "galactic tank force" wins over "tank".
        var containment = new List<MachineVariant>();
        var bestLength = 0;
        foreach (var v in _index.AllLongestFirst())
        {
            if (v.Tokens.Count < bestLength) break; // sorted longest-first: no better match remains
            if (!IsEligible(v, query, ResolutionStage.Containment)) continue;
            if (!ContainsSequence(tokens, v.Tokens)) continue;

            if (v.Tokens.Count > bestLength)
            {
                bestLength = v.Tokens.Count;
                containment.Clear();
            }
            containment.Add(v);
        }

        var scoped = Scope(containment, query);
        return scoped.Count > 0
            ? Decide(scoped, query, ResolutionStage.Containment)
            : new ResolutionResult.NoMatch();
    }

    private List<MachineVariant> Eligible(IReadOnlyList<MachineVariant> candidates, ResolutionQuery q, ResolutionStage stage)
        => Scope(candidates.Where(v => IsEligible(v, q, stage)).ToList(), q);

    // The single-word guard lives here, as a policy rule rather than a hole in the index.
    //
    // Two variant classes are blocked from Stage 3 containment:
    //
    // 1. ManufacturerPrefixed variants ("stern pinball") — they always include the manufacturer
    //    name as the first token, so any manufacturer-branded document title would spuriously
    //    match. "Stern Pinball Service Bulletin" would bind to the "Pinball" machine via the
    //    "stern pinball" ManufacturerPrefixed variant if containment were allowed.
    //    They remain eligible for Exact and FranchisePrefix point-reads.
    //
    // 2. Single-token trailing-qualifier variants ("pinball", "edition", etc.) — the canonical
    //    instance of the 172-document incident. "pinball" appears in filenames and page text for
    //    almost every document; a machine titled "Pinball" must not absorb them all.
    //    Note: single-token variants whose key is NOT a trailing qualifier (e.g., "godzilla",
    //    "houdini" derived from "Houdini: Master of Mystery") ARE eligible for containment —
    //    they are specific enough to be useful identifiers.
    private static bool IsEligible(MachineVariant v, ResolutionQuery q, ResolutionStage stage)
    {
        if (stage == ResolutionStage.Exact) return true;
        if (stage == ResolutionStage.Containment && v.Kind == VariantKind.ManufacturerPrefixed) return false;
        if (v.IsSingleToken && TrailingQualifiers.Contains(v.Tokens[0])) return false;
        return true;
    }

    private List<MachineVariant> Scope(List<MachineVariant> candidates, ResolutionQuery q)
    {
        if (candidates.Count == 0 || q.ManufacturerHint is null) return candidates;

        var matching = candidates
            .Where(v => string.Equals(v.ManufacturerKey, q.ManufacturerHint, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (IsFuzzy(q.EvidenceKind)) return matching;                 // hard filter
        return matching.Count > 0 ? matching : candidates;            // soft preference
    }

    private ResolutionResult Decide(List<MachineVariant> candidates, ResolutionQuery q, ResolutionStage stage)
    {
        var first = candidates[0];
        var evidence = new ResolutionEvidence(q.EvidenceKind, first.Kind, first.Key, stage);

        var machineIds = candidates.Select(v => v.MachineId).Distinct(StringComparer.Ordinal).ToList();
        if (machineIds.Count == 1) return new ResolutionResult.Resolved(machineIds[0], evidence);

        var groups = candidates.Select(v => v.GroupId).Distinct(StringComparer.Ordinal).ToList();
        if (groups.Count == 1 && groups[0] is { } groupId)
            return new ResolutionResult.ResolvedFamily(groupId, machineIds, evidence);

        var cands = machineIds
            .Select(id =>
            {
                var v = candidates.First(c => c.MachineId == id);
                var title = _machines.TryGetValue(id, out var m) ? m.Title : id;
                return new ResolutionCandidate(id, title, v.Kind, v.Key);
            })
            .ToList();

        return new ResolutionResult.Ambiguous(cands, evidence);
    }

    private static bool ContainsSequence(IReadOnlyList<string> haystack, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || needle.Count > haystack.Count) return false;
        for (var i = 0; i + needle.Count <= haystack.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (!string.Equals(haystack[i + j], needle[j], StringComparison.Ordinal)) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }
}
