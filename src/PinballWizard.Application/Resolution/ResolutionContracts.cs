namespace PinballWizard.Application.Resolution;

// What KIND of text we are matching. The resolver's manufacturer scoping and
// single-token eligibility both key off this (ADR-0054).
public enum EvidenceKind
{
    ProvenanceSlug,  // raw.Game.Slug — the scraper's own claim; high trust
    Filename,        // fuzzy: a filename may mention any machine
    PageText,        // fuzzy: page-1/2 extracted text
    ScrapedTitle,    // a scraped GameRecord title (reconciler)
    FreeText,        // a user/agent query (getMachineByTitle)
}

// Where a matchable variant came from.
public enum VariantKind
{
    FullTitle,
    FranchiseTitle,        // subtitle stripped: "Houdini: Master of Mystery" → "houdini"
    TitleWithEdition,
    ManufacturerPrefixed,
    ScraperSlug,           // Machine.ManufacturerSlugs — now ONE evidence source, not the join key
    CuratedAlias,          // machine_aliases.v1.json
}

public enum ResolutionStage { Exact, FranchisePrefix, Containment, None }

public sealed record MachineVariant
{
    private MachineVariant(string key, IReadOnlyList<string> tokens, VariantKind kind,
        string machineId, string manufacturerKey, string? groupId)
    {
        Key = key; Tokens = tokens; Kind = kind;
        MachineId = machineId; ManufacturerKey = manufacturerKey; GroupId = groupId;
    }

    public string Key { get; }
    public IReadOnlyList<string> Tokens { get; }
    public VariantKind Kind { get; }
    public string MachineId { get; }
    public string ManufacturerKey { get; }
    public string? GroupId { get; }

    public bool IsSingleToken => Tokens.Count == 1;

    // The ONLY way to build a variant — guarantees every key went through the one normalizer.
    public static MachineVariant Create(string text, VariantKind kind,
        string machineId, string manufacturerKey, string? groupId)
    {
        var tokens = MachineTextNormalizer.Tokenize(text);
        if (tokens.Count == 0)
            throw new ArgumentException($"Text '{text}' normalizes to zero tokens.", nameof(text));
        return new MachineVariant(string.Join(' ', tokens), tokens, kind, machineId, manufacturerKey, groupId);
    }
}

public sealed record ResolutionQuery(string Text, EvidenceKind EvidenceKind, string? ManufacturerHint = null);

public sealed record ResolutionEvidence(
    EvidenceKind EvidenceKind, VariantKind VariantKind, string MatchedVariant, ResolutionStage Stage);

public sealed record ResolutionCandidate(
    string MachineId, string MachineTitle, VariantKind VariantKind, string MatchedVariant);

public abstract record ResolutionResult
{
    private ResolutionResult() { }

    public sealed record Resolved(string MachineId, ResolutionEvidence Evidence) : ResolutionResult;

    // One edition family (single distinct GroupId) — all siblings are legitimate targets.
    public sealed record ResolvedFamily(
        string GroupId, IReadOnlyList<string> MachineIds, ResolutionEvidence Evidence) : ResolutionResult;

    // Multiple non-family candidates. The resolver NEVER picks one — this becomes needs_review.
    public sealed record Ambiguous(
        IReadOnlyList<ResolutionCandidate> Candidates, ResolutionEvidence Evidence) : ResolutionResult;

    public sealed record NoMatch : ResolutionResult;
}

public interface IMachineResolver
{
    ResolutionResult Resolve(ResolutionQuery query);
}
