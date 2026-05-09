using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Application.Landing;

// PR-L2 implementation of ILandingService. Returns LandingResponse with
// SeedQuestions populated from ISeedQuestionLoader and FeaturedMachines
// populated from IFeaturedMachineRepository (Cosmos featured_machines
// container). SystemStatus remains null until PR-L3 (/api/wizard/landing
// endpoint + SystemStatus composition) lands.
//
// Registered as a singleton (see ServiceCollectionExtensions). The seed
// questions JSON is static between deploys; the featured machines are
// a small curated set (~6) so the per-request cost is bounded.
//
// IFeaturedMachineRepository is an optional dependency: the service
// degrades gracefully to null FeaturedMachines when Cosmos is not
// configured (e.g., PinballWizard.Api started in local dev without an
// emulator). This matches the PR-L1 null-placeholder contract and prevents
// the API from failing to start when only Foundry or no Cosmos is wired.
public sealed class LandingService : ILandingService
{
    private readonly ISeedQuestionLoader _seedQuestionLoader;
    private readonly IFeaturedMachineRepository? _featuredMachineRepository;
    private readonly ILogger<LandingService> _logger;

    public LandingService(
        ISeedQuestionLoader seedQuestionLoader,
        ILogger<LandingService> logger,
        IFeaturedMachineRepository? featuredMachineRepository = null)
    {
        ArgumentNullException.ThrowIfNull(seedQuestionLoader);
        ArgumentNullException.ThrowIfNull(logger);
        _seedQuestionLoader = seedQuestionLoader;
        _featuredMachineRepository = featuredMachineRepository;
        _logger = logger;
    }

    public async Task<LandingResponse> GetLandingAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Loading landing response: seed questions + featured machines " +
            "(Cosmos repo available: {CosmosAvailable}).",
            _featuredMachineRepository is not null);

        // Fan-out the two calls when Cosmos is configured so the combined
        // latency is max(seed_load, cosmos_read) rather than the sum.
        IReadOnlyList<SeedQuestion> seedQuestions;
        IReadOnlyList<FeaturedMachine>? featuredMachines = null;

        if (_featuredMachineRepository is not null)
        {
            var seedQuestionsTask = _seedQuestionLoader.LoadAsync(cancellationToken);
            var featuredMachinesTask = _featuredMachineRepository.GetAllAsync(cancellationToken);

            await Task.WhenAll(seedQuestionsTask, featuredMachinesTask).ConfigureAwait(false);

            seedQuestions = seedQuestionsTask.Result;
            var fetched = featuredMachinesTask.Result;
            featuredMachines = fetched.Count > 0 ? fetched : null;
        }
        else
        {
            seedQuestions = await _seedQuestionLoader
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Landing response assembled: {SeedQuestionCount} seed question(s), " +
            "{FeaturedMachineCount} featured machine(s), SystemStatus=null (PR-L3).",
            seedQuestions.Count, featuredMachines?.Count ?? 0);

        return new LandingResponse(
            SeedQuestions: seedQuestions,
            FeaturedMachines: featuredMachines,
            SystemStatus: null);
    }
}
