namespace PinballWizard.Application.Ai;

// Per ADR-0026 § 4. Recovery payload that lets RefusalPanel.razor render
// clickable plural community-resource recovery + confidence breakdown,
// instead of generic refusal prose. Fields are nullable because Wave 1
// only ships the shape — Wave 2 PR-R2/R3/R4 fill RelatedMachines,
// CommunityResources, MissingWhat, SuggestedRephrase. The shape is
// stable across that addition; AiRouter's WizardAnswer construction
// supplies a non-null RefusalDetail with empty/null sub-fields when
// IsRefusal=true, and null when IsRefusal=false.
public sealed record RefusalDetail(
    ConfidenceBreakdown? Confidence,
    IReadOnlyList<RelatedMachine>? RelatedMachines,
    IReadOnlyList<CommunityResource>? CommunityResources,
    string? MissingWhat,
    string? SuggestedRephrase);

// Surfaced from ConfidenceSignals (already computed in AiRouter.cs ~line
// 211 via IConfidenceCalculator.Compute). Composite is the geometric
// mean per ADR-0017 § Algorithm. Threshold is the cutoff that fired the
// refusal (today AiFoundryOptions.ConfidenceThreshold default 0.65).
public sealed record ConfidenceBreakdown(
    double RetrievalSimilarity,
    double ModelSelfReported,
    double CitationCoverage,
    double Composite,
    double Threshold);

// Stub records — populated in Wave 2. Keeping them here in the same
// file is intentional: Wave 1 is shape, Wave 2 is fill. Splitting into
// separate files is the Wave 2 PR-R2 job once IRefusalRecoveryService
// arrives.
public sealed record RelatedMachine(
    string MachineId,
    string Title,
    string? OpdbUrl);

public sealed record CommunityResource(
    string Name,
    string Url,
    string Category,
    string? Description);
