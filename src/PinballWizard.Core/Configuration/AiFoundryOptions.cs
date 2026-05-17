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
    // Wave 2 IAiRouter Wizard agent's default model. Per ADR-0015 (amended
    // 2026-05-17), the default is gpt-4o (Standard SKU, version 2024-11-20)
    // rather than gpt-4o-mini — gpt-4o-mini 2024-07-18 is deprecated for new
    // Standard deployments, and gpt-4o produces measurably better citation
    // fidelity and structured-output reliability for the showcase quality bar.
    // Individual sub-agents may override via AgentModels[<name>].
    public string ChatDeploymentName { get; set; } = "gpt-4o";

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

    // Below this confidence score, IAiRouter returns a refusal rather
    // than the agent's text. Per ADR-0017 the initial draft is 0.65;
    // calibrated against the eval-set at H2 hand-off (scope item 13).
    // If the calibrated value moves >0.05 from this draft, ADR-0017
    // gets a follow-up entry recording the post-calibration value.
    [Range(0.0, 1.0)]
    public double ConfidenceThreshold { get; set; } = 0.65;

    // Phase 4 W1-2 cutover flag (ADR-0022). When true, the legacy regex
    // citation extractor runs in parallel with the tool-trace extractor;
    // its citation count is emitted under
    // pinwiz.ai.citations.extracted_total{source=regex_legacy} alongside
    // the tool_trace count. Default true so the cutover window has
    // observability data; flipped to false in a follow-up PR after H2
    // baseline confirms the tool-trace extractor produces parity-or-better
    // citation_precision, at which point RegexLegacyCitationExtractor +
    // this flag are both deleted.
    public bool RetainRegexCitationCutover { get; set; } = true;

    // USD-cent pricing per 1k input + output tokens, keyed by deployment
    // name (per ADR-0015's per-agent model selection). Populated with
    // 2026 May Azure OpenAI public pricing for the deployments shipped
    // by the H2 hand-off (gpt-4o, gpt-4-1, text-embedding-3-large);
    // operators override via configuration when prices change. Empty
    // dictionary disables cost attribution (cost_usd_cents always 0).
    public Dictionary<string, ModelPricing> PricingTable { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // gpt-4o Standard (2024-11-20): $2.50 / 1M input ≈ 0.25 cents/1k;
        // $10.00 / 1M output ≈ 1.00 cents/1k.
        ["gpt-4o"] = new ModelPricing(InputCentsPer1K: 0.25, OutputCentsPer1K: 1.00),
        // gpt-4.1 Standard (deployment name 'gpt-4-1' due to
        // Foundry's no-dot rule): $2.00 / 1M input ≈ 0.20 cents/1k;
        // $8.00 / 1M output ≈ 0.80 cents/1k.
        ["gpt-4-1"] = new ModelPricing(InputCentsPer1K: 0.20, OutputCentsPer1K: 0.80),
        // text-embedding-3-large Standard: $0.13 / 1M tokens ≈ 0.013
        // cents/1k. Embeddings have no "output" token concept; we
        // record the same rate on both fields so the cost calculator
        // is uniform.
        ["text-embedding-3-large"] = new ModelPricing(InputCentsPer1K: 0.013, OutputCentsPer1K: 0.013),
    };
}

// Per-deployment pricing fact, keyed by deployment name in
// AiFoundryOptions.PricingTable. USD cents per 1,000 tokens (industry
// units; Microsoft publishes pricing per 1M tokens, our config converts
// down to keep the cost-counter values readable in dashboards).
public sealed record ModelPricing(double InputCentsPer1K, double OutputCentsPer1K);
