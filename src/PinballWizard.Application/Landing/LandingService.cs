using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Application.Landing;

// PR-L3 implementation of ILandingService. Returns LandingResponse with
// all three fields populated:
//   SeedQuestions  — from ISeedQuestionLoader (static JSON)
//   FeaturedMachines — from IFeaturedMachineRepository (Cosmos featured_machines)
//   SystemStatus   — from ISystemStatusProvider (Foundry + AI Search + Cosmos canary)
//
// All three calls fan-out via Task.WhenAll so the combined latency is
// max(seed_load, cosmos_read, status_probe) rather than the sum.
// ISystemStatusProvider is stampede-safe and caches its result for 30 s
// (default), so the per-request cost is effectively zero on cache hits.
//
// Registered as a singleton (see ServiceCollectionExtensions). Optional
// dependencies degrade gracefully:
//   IFeaturedMachineRepository absent → FeaturedMachines = null
//   ISystemStatusProvider absent      → SystemStatus = null
// This allows the Api to start cleanly in local dev without Cosmos or
// Foundry configured.
public sealed class LandingService : ILandingService
{
    private readonly ISeedQuestionLoader _seedQuestionLoader;
    private readonly IFeaturedMachineRepository? _featuredMachineRepository;
    private readonly ISystemStatusProvider? _systemStatusProvider;
    private readonly ILogger<LandingService> _logger;

    public LandingService(
        ISeedQuestionLoader seedQuestionLoader,
        ILogger<LandingService> logger,
        IFeaturedMachineRepository? featuredMachineRepository = null,
        ISystemStatusProvider? systemStatusProvider = null)
    {
        ArgumentNullException.ThrowIfNull(seedQuestionLoader);
        ArgumentNullException.ThrowIfNull(logger);
        _seedQuestionLoader = seedQuestionLoader;
        _featuredMachineRepository = featuredMachineRepository;
        _systemStatusProvider = systemStatusProvider;
        _logger = logger;
    }

    public async Task<LandingResponse> GetLandingAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Loading landing response: seed questions + featured machines + system status " +
            "(Cosmos repo: {CosmosAvailable}, StatusProvider: {StatusAvailable}).",
            _featuredMachineRepository is not null,
            _systemStatusProvider is not null);

        // Fan-out all three calls so the combined latency is
        // max(seed_load, cosmos_read, status_probe) not the sum.
        var seedQuestionsTask = _seedQuestionLoader.LoadAsync(cancellationToken);

        var featuredMachinesTask = _featuredMachineRepository is not null
            ? _featuredMachineRepository.GetAllAsync(cancellationToken)
            : Task.FromResult<IReadOnlyList<FeaturedMachine>>(null!);

        var systemStatusTask = _systemStatusProvider is not null
            ? _systemStatusProvider.GetStatusAsync(cancellationToken)
            : Task.FromResult<SystemStatus>(null!);

        await Task.WhenAll(seedQuestionsTask, featuredMachinesTask, systemStatusTask)
            .ConfigureAwait(false);

        var seedQuestions = seedQuestionsTask.Result;
        var fetchedMachines = featuredMachinesTask.Result;
        var featuredMachines = fetchedMachines is { Count: > 0 } ? fetchedMachines : null;
        var systemStatus = systemStatusTask.Result;

        _logger.LogDebug(
            "Landing response assembled: {SeedQuestionCount} seed question(s), " +
            "{FeaturedMachineCount} featured machine(s), " +
            "SystemStatus=[Cosmos={CosmosHealthy}, Foundry={FoundryHealthy}, AiSearch={AiSearchHealthy}].",
            seedQuestions.Count,
            featuredMachines?.Count ?? 0,
            systemStatus?.CosmosHealthy?.ToString() ?? "null",
            systemStatus?.FoundryHealthy?.ToString() ?? "null",
            systemStatus?.AiSearchHealthy?.ToString() ?? "null");

        return new LandingResponse(
            SeedQuestions: seedQuestions,
            FeaturedMachines: featuredMachines,
            SystemStatus: systemStatus);
    }
}
