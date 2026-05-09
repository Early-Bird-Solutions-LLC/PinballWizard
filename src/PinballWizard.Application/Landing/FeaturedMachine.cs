namespace PinballWizard.Application.Landing;

// Placeholder type for PR-L1. PR-L2 wires the Cosmos lookup that
// populates FeaturedMachines on LandingResponse. Shipping the type now
// prevents LandingResponse from churning shape when L2 lands.
public sealed record FeaturedMachine(
    string MachineId,
    string Title,
    string? OpdbId);
