namespace PinballWizard.Application.Ai.Tools;

// DTO returned by the getKineticistGuideLinks Foundry function tool (ADR-0043 Tier B).
//
// Carries URL deep-links only — never guide body text (Tier C1 is not available).
// ToolTraceCitationExtractor probes for FullTutorialLink / GameRulesLink /
// PinballPrimerUrl to recognize this shape and emits CitationSourceType.CuratedLink
// citations for every non-empty URL (provenance is sacred; Kineticist attribution
// rides every Tier B answer).
//
// Null return from GetKineticistGuideLinksAsync means no deep-links were available
// (key absent, game unknown, or API fields empty). Fail closed — never silently
// fall back to scraping the challenged .md endpoint.
public sealed record KineticistGuideLinksDto(
    string? MachineTitle,
    string? OpdbId,
    string? KineticistSlug,
    // Distinctive probe fields — unique to this DTO vs MarketValueDto /
    // MachineGroundingDto / SearchCorpusResult.
    string? FullTutorialLink,
    string? GameRulesLink,
    string? PinballPrimerUrl,
    string? AttributionUrl,
    string? AttributionText);
