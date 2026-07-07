using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Ai.Hosting;

// The closed set of runtime-mutable settings (admin settings plan, PR-B1).
//
// Each key maps a Cosmos admin_settings override onto an AiFoundryOptions
// or RetrievalOptions property. A key exists here ONLY once something reads
// it at runtime — shipping a key nothing consumes is the dead-config smell
// /local-review exists to catch. Deliberately NOT here yet:
//   - ChatDeploymentName / AgentModels: FoundryAgentFactory caches built
//     agents; runtime model changes need the cache-invalidation hook that
//     ships with prompt templates (plan Phase 3).
//   - SemanticCacheMaxEntries: consumed at cache construction; a runtime
//     change cannot resize the live LRU. Applies at restart only — wire it
//     when the settings page can say so honestly.
//   - EmbeddingDeploymentName: excluded permanently (changing it requires
//     a full re-index; deliberately not runtime-mutable).
public static class WellKnownSettings
{
    public const string ConfidenceThreshold = "ai.confidence_threshold";
    public const string PerCallCostCeilingUsdCents = "ai.per_call_cost_ceiling_usd_cents";
    public const string MaxConversationTurns = "ai.max_conversation_turns";

    // Retrieval tuning keys (PR retrieval-runtime-keys). Both consumed at
    // searchCorpus call time so a stored override takes effect on the next
    // ask without a restart.
    public const string RetrievalTopK = "rag.retrieval_top_k";
    public const string RetrievalMinimumScore = "rag.retrieval_minimum_score";

    // Validation ranges, enforced server-side at write time (the page
    // mirrors them client-side). Bounds rationale:
    //   confidence: below 0.3 the gate stops gating; above 0.95 nearly
    //     every answer refuses (ADR-0017's threshold semantics).
    //   ceiling: 1¢ floor keeps the gate meaningful; 100¢ cap bounds the
    //     worst-case per-ask spend at $1 (ADR-0015 cost posture).
    //   turns: 1..20 — the API request guard rejects >20-turn history, so
    //     a router cap above it could never be exercised.
    //   retrieval_top_k: 1..20 — floor of 1 keeps retrieval meaningful;
    //     ceiling of 20 matches SearchCorpusTool.TopKCeiling (server-side
    //     clamp on the model-requested value). AI Search has no semantic
    //     re-ranking benefit past ~20 candidates (ADR-0021 § Search defaults).
    //   retrieval_minimum_score: 0.0..1.0 — a NORMALIZED fraction of the
    //     reranker ceiling, equal to the citation "% match" / 100. The raw
    //     Azure semantic reranker score is 0–4 (RetrievalScoring.MaxRerankerScore);
    //     the retriever normalizes via RetrievalScoring.NormalizeRerankerScore
    //     before comparing to this floor, so 0.35 here means "cut anything
    //     below 35% match". 0.0 returns every hit; 1.0 keeps only a perfect
    //     match. Live default is 0.35 (2026-07-06 design); code default stays
    //     0.0 for CLI/fixtures.
    public static readonly IReadOnlyDictionary<string, (double Min, double Max)> NumericRanges =
        new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            [ConfidenceThreshold] = (0.3, 0.95),
            [PerCallCostCeilingUsdCents] = (1, 100),
            [MaxConversationTurns] = (1, 20),
            [RetrievalTopK] = (1, 20),
            [RetrievalMinimumScore] = (0.0, 1.0),
        };

    public static IReadOnlyList<string> AllKeys { get; } =
    [
        ConfidenceThreshold,
        PerCallCostCeilingUsdCents,
        MaxConversationTurns,
        RetrievalTopK,
        RetrievalMinimumScore,
    ];

    // Server-side write validation: unknown keys and out-of-range values
    // are rejected before they reach the store (a malformed stored value
    // would otherwise degrade-to-default on every read — see
    // RuntimeSettings). Returns false with a human-readable reason.
    public static bool TryValidate(string key, string value, out string? error)
    {
        if (!NumericRanges.TryGetValue(key, out var range))
        {
            error = $"'{key}' is not a runtime-mutable setting.";
            return false;
        }

        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"'{value}' is not a number.";
            return false;
        }

        if (parsed < range.Min || parsed > range.Max)
        {
            error = $"{key} must be between {range.Min} and {range.Max} (got {parsed}).";
            return false;
        }

        error = null;
        return true;
    }

    // The IOptions/default for a key — what the Wizard uses when no
    // override is stored, and what "reset to default" reverts to.
    // Keys backed by AiFoundryOptions are resolved from the live options
    // object; retrieval keys use the RetrievalOptions record defaults,
    // which are compile-time constants and do not vary per deployment.
    public static string DefaultFor(string key, AiFoundryOptions options) => key switch
    {
        ConfidenceThreshold => options.ConfidenceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PerCallCostCeilingUsdCents => options.PerCallCostCeilingUsdCents.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MaxConversationTurns => options.MaxConversationTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // Retrieval defaults are the RetrievalOptions record parameter
        // defaults (TopK=10, MinimumScore=0.0 per ADR-0021 § Search defaults).
        // They are not sourced from AiFoundryOptions — the options parameter
        // is not used for these keys, but the single-signature convention is
        // kept so callers iterate AllKeys without branching.
        RetrievalTopK => new RetrievalOptions().TopK.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RetrievalMinimumScore => new RetrievalOptions().MinimumScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a well-known setting."),
    };
}
