using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the Microsoft Foundry AI orchestration layer (ADR-0014).
// Phase 3 introduces this surface; consumed by AzureFoundrySmokeProbe in
// Wave 1 and by IFoundryAgentFactory + IAiRouter in Wave 2 PR 4.
//
// Sectioning convention matches OpdbOptions / PinballMapOptions / CosmosOptions:
// SectionName + per-key constants for presence-checking from gating code.
public sealed class AiFoundryOptions
{
    public const string SectionName = "AiFoundry";

    public const string ProjectEndpointKey = $"{SectionName}:{nameof(ProjectEndpoint)}";

    // The Foundry project endpoint URL, e.g.
    //   https://<account>.services.ai.azure.com/api/projects/<project>
    // Per ADR-0014, hub-based projects are discontinued; this MUST be a
    // project-endpoint URL (not a hub URL or a connection string).
    [Url]
    public string ProjectEndpoint { get; set; } = string.Empty;

    // Default chat-completion deployment name in the Foundry project.
    // Surfaced as the smoke probe's expected chat deployment and as the
    // Wave 2 IAiRouter Wizard agent's default model. Per ADR-0015, individual
    // sub-agents may override via AgentModels[<name>].
    public string ChatDeploymentName { get; set; } = "gpt-4o-mini";

    // Default embedding deployment name. Used for semantic-cache key
    // generation (Wave 2) and for Phase 4 RAG embedding. Per ADR-0014,
    // text-embedding-3-large at 3072 dimensions is the locked choice.
    public string EmbeddingDeploymentName { get; set; } = "text-embedding-3-large";

    // Optional Foundry guardrail/content-safety policy name (server-side
    // configured). When unset, the Foundry project's default policy applies.
    // Per ADR-0017, content-safety refusals surface via Foundry's response
    // metadata as RefusalCategory.HarmfulContent.
    public string? GuardrailName { get; set; }

    // Per-agent model overrides (keyed by agent name: "Wizard", "Valuation",
    // "Rules", "Repair"). Per ADR-0015's per-agent cost-routing strategy.
    // Empty by default; populated in Wave 2 PR 4 as agents are registered.
    public Dictionary<string, string> AgentModels { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // Per-call cost ceiling in USD cents (default 10 = $0.10). Per ADR-0015,
    // the IAiRouter refuses with RefusalCategory.CostCeilingHit before
    // exceeding this on a single user-question. Generous default; calibrated
    // empirically at H2 hand-off.
    [Range(1, 1000)]
    public int PerCallCostCeilingUsdCents { get; set; } = 10;

    // In-process LRU semantic-answer cache capacity (per ADR-0015). 512
    // entries is sufficient for v1 single-instance scale.
    [Range(0, 10000)]
    public int SemanticCacheMaxEntries { get; set; } = 512;
}
