namespace PinballWizard.Application.Landing;

// Per ADR-0026 § Landing surface. PR-L1 populates SeedQuestions; the
// other two fields are null until PR-L2 (FeaturedMachines Cosmos lookup)
// and PR-L3 (/api/wizard/landing endpoint + SystemStatus composition)
// land respectively.
public sealed record LandingResponse(
    IReadOnlyList<SeedQuestion> SeedQuestions,
    IReadOnlyList<FeaturedMachine>? FeaturedMachines = null,
    SystemStatus? SystemStatus = null);
