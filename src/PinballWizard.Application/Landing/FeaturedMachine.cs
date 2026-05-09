namespace PinballWizard.Application.Landing;

// Landing-page featured machine DTO. PR-L1 shipped the placeholder shape;
// PR-L2 widens it with DisplayOrder and Tagline (populated from the
// featured_machines Cosmos container) and wires FeaturedMachines on
// LandingResponse via IFeaturedMachineRepository.
//
// MachineId = the slug (partition key / id in the Cosmos document).
// DisplayOrder drives sort order on the landing strip (ascending).
// Tagline is showcase-quality marketing copy visible to prospects on first
// contact with the application — never blank in practice (validation in
// FeaturedMachineSeedLoader rejects blank taglines), but nullable so the
// record can represent a degraded read from a pre-L2 doc without throwing.
public sealed record FeaturedMachine(
    string MachineId,
    string Title,
    string? OpdbId,
    int DisplayOrder = 0,
    string? Tagline = null);
