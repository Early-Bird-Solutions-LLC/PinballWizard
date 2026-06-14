using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Confidence;
using PinballWizard.Application.Ai.Cost;
using PinballWizard.Application.Ai.Evaluation.Evaluators;
using PinballWizard.Application.Ai.Refusal;
using PinballWizard.Application.Ai.Tools;

namespace PinballWizard.Application.Ai;

public static class ServiceCollectionExtensions
{
    // Wires the Application-layer AI components: prompt provider, cache,
    // router, function tools. The IFoundryAgentFactory implementation
    // lives in Infrastructure (since it depends on
    // Microsoft.Agents.AI.Foundry + Azure.AI.Projects); the
    // Infrastructure DI extension calls AddAiRouter as part of
    // AddAzureFoundryIntegration to ensure the router and its factory
    // ship together.
    public static IServiceCollection AddAiRouter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // EmbeddedResourceAgentPromptProvider is registered concretely so
        // OverridingAgentPromptProvider can inject it directly (not via the
        // IAgentPromptProvider abstraction, which would create a circular
        // dependency). OverridingAgentPromptProvider is the live
        // IAgentPromptProvider that layers the Cosmos override store on top.
        // On hosts without Cosmos (standalone CLI, test fixtures),
        // IAgentPromptOverrideRepository resolves as null and
        // OverridingAgentPromptProvider degrades to embedded-resource-only
        // behaviour — identical to pre-PR-B3.
        services.TryAddSingleton<EmbeddedResourceAgentPromptProvider>();
        services.TryAddSingleton<IAgentPromptProvider, OverridingAgentPromptProvider>();
        services.TryAddSingleton<ISemanticAnswerCache, SemanticAnswerCache>();
        // Wave 2 PR-D2: per-call ambient degradation context. Singleton backed
        // by AsyncLocal<T> — same pattern as IHttpContextAccessor. Safe to inject
        // into other singletons (SearchCorpusTool, AiRouter) because state is
        // flow-local, not instance-shared.
        services.TryAddSingleton<IDegradationContext, AmbientDegradationContext>();

        // UI-metadata side channel (fix/citation-metadata-channel): request-scoped
        // store that carries Score + LastScrapedUtc from SearchCorpusTool to
        // ToolTraceCitationExtractor. These fields are [JsonIgnore] on
        // SearchCorpusHit (model must not see retrieval internals), so they are
        // stripped from FunctionResultContent.Result JSON on the real Foundry path.
        // The sink bridges that gap without exposing the fields to the model.
        // Scoped so each HTTP request / streaming turn gets its own clean store.
        services.TryAddScoped<IRetrievalCitationMetadataSink, RetrievalCitationMetadataSink>();

        services.TryAddSingleton<MachineGroundingTool>();
        services.TryAddSingleton<SearchCorpusTool>();
        services.TryAddSingleton<IConfidenceCalculator, ConfidenceCalculator>();
        services.TryAddSingleton<ITokenUsageReader, NullTokenUsageReader>();
        services.TryAddSingleton<IAiCostCalculator, AiCostCalculator>();

        // Citation extractors (ADR-0022). Both impls register concretely
        // (not via ICitationExtractor) — AiRouter ctor takes them by
        // concrete type because the cutover semantics aren't symmetric
        // (tool-trace is the authoritative source; regex_legacy is
        // telemetry-only). Once the cutover flag is removed in a
        // follow-up PR, RegexLegacyCitationExtractor + this registration
        // both go away and AiRouter takes ICitationExtractor.
        services.TryAddSingleton<ToolTraceCitationExtractor>();
        services.TryAddSingleton<RegexLegacyCitationExtractor>();

        // Wave 2 PR-R3 community resource loader: reads community_resources.v1.json
        // once at startup (lazy singleton) and serves per-category resource lists
        // to RefusalRecoveryService. Plurality minimums (marketplace ≥ 3,
        // machine_reference ≥ 2) enforced at load time — fail-fast on broken seed.
        services.TryAddSingleton<ICommunityResourceLoader, CommunityResourceLoader>();

        // Wave 2 PR-R2/R3 recovery service: populates RefusalDetail.RelatedMachines
        // (up to 3 machines by token-overlap) and RefusalDetail.CommunityResources
        // (curated per-category resource cards) on refusal answers. Singleton.
        // Registered before IAiRouter so the router resolves it on first construction.
        services.TryAddSingleton<IRefusalRecoveryService, RefusalRecoveryService>();

        services.TryAddSingleton<IAiRouter, AiRouter>();

        // Evaluation harness evaluators (ADR-0016). Pure deterministic
        // logic — singletons. Registered alongside the router so
        // anywhere the router is wired, the eval primitives are
        // available too. The IEvaluationHarness implementation that
        // composes them lives in Infrastructure (depends on
        // Azure.AI.Projects for evaluator-definition registration).
        services.TryAddSingleton<CitationPrecisionEvaluator>();
        services.TryAddSingleton<CitationRecallEvaluator>();
        services.TryAddSingleton<CitationCoverageEvaluator>();
        services.TryAddSingleton<SubagentAccuracyEvaluator>();
        services.TryAddSingleton<RefusalCorrectnessEvaluator>();

        // Edition-aware evaluators (AB#259, edition-scope-model-design §6).
        // R2 (answer differs by edition → one attributed response) and R3
        // (named edition absent → honest substitution). Same pure-singleton
        // shape as the four above.
        services.TryAddSingleton<AnsweredAllEditionsEvaluator>();
        services.TryAddSingleton<HonestSubstitutionEvaluator>();

        return services;
    }
}
