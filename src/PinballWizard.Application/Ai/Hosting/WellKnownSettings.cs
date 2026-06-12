using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Ai.Hosting;

// The closed set of runtime-mutable settings (admin settings plan, PR-B1).
//
// Each key maps a Cosmos admin_settings override onto an AiFoundryOptions
// property. A key exists here ONLY once something reads it at runtime —
// shipping a key nothing consumes is the dead-config smell /local-review
// exists to catch. Deliberately NOT here yet:
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

    // Validation ranges, enforced server-side at write time (the page
    // mirrors them client-side). Bounds rationale:
    //   confidence: below 0.3 the gate stops gating; above 0.95 nearly
    //     every answer refuses (ADR-0017's threshold semantics).
    //   ceiling: 1¢ floor keeps the gate meaningful; 100¢ cap bounds the
    //     worst-case per-ask spend at $1 (ADR-0015 cost posture).
    //   turns: 1..20 — the API request guard rejects >20-turn history, so
    //     a router cap above it could never be exercised.
    public static readonly IReadOnlyDictionary<string, (double Min, double Max)> NumericRanges =
        new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            [ConfidenceThreshold] = (0.3, 0.95),
            [PerCallCostCeilingUsdCents] = (1, 100),
            [MaxConversationTurns] = (1, 20),
        };

    public static IReadOnlyList<string> AllKeys { get; } =
    [
        ConfidenceThreshold,
        PerCallCostCeilingUsdCents,
        MaxConversationTurns,
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

    // The IOptions default for a key — what the Wizard uses when no
    // override is stored, and what "reset to default" reverts to.
    public static string DefaultFor(string key, AiFoundryOptions options) => key switch
    {
        ConfidenceThreshold => options.ConfidenceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PerCallCostCeilingUsdCents => options.PerCallCostCeilingUsdCents.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MaxConversationTurns => options.MaxConversationTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a well-known setting."),
    };
}
