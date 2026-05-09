using Microsoft.Extensions.Logging;

namespace PinballWizard.Application.Landing;

// PR-L1 implementation of ILandingService. Returns LandingResponse with
// SeedQuestions populated from ISeedQuestionLoader; FeaturedMachines and
// SystemStatus are null until PR-L2 (Cosmos featured_machines lookup)
// and PR-L3 (/api/wizard/landing endpoint + SystemStatus composition)
// land respectively.
//
// Registered as a singleton (see ServiceCollectionExtensions). The seed
// questions JSON is static between deploys, so the load cost is paid once
// at first call via the underlying file read in SeedQuestionLoader.
public sealed class LandingService : ILandingService
{
    private readonly ISeedQuestionLoader _seedQuestionLoader;
    private readonly ILogger<LandingService> _logger;

    public LandingService(
        ISeedQuestionLoader seedQuestionLoader,
        ILogger<LandingService> logger)
    {
        ArgumentNullException.ThrowIfNull(seedQuestionLoader);
        ArgumentNullException.ThrowIfNull(logger);
        _seedQuestionLoader = seedQuestionLoader;
        _logger = logger;
    }

    public async Task<LandingResponse> GetLandingAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Loading landing response: seed questions.");

        var seedQuestions = await _seedQuestionLoader
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Landing response assembled: {SeedQuestionCount} seed question(s), " +
            "FeaturedMachines=null (PR-L2), SystemStatus=null (PR-L3).",
            seedQuestions.Count);

        return new LandingResponse(
            SeedQuestions: seedQuestions,
            FeaturedMachines: null,
            SystemStatus: null);
    }
}
