namespace PinballWizard.Application.Landing;

// Entry point for the /api/wizard/landing endpoint (wired in PR-L3).
// PR-L1 ships the interface + implementation shell: SeedQuestions are
// populated; FeaturedMachines and SystemStatus are null placeholders.
public interface ILandingService
{
    Task<LandingResponse> GetLandingAsync(CancellationToken cancellationToken);
}
