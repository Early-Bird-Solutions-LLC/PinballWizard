using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Ai.Hosting;

// Read-side facade for runtime-mutable settings (admin settings plan,
// PR-B1). One snapshot per ask: AiRouter calls GetSnapshotAsync at the top
// of AnswerAsync/AnswerStreamingAsync so a single answer is internally
// consistent even if an admin saves mid-stream.
//
// Layering rule (the whole point): stored override → IOptions default.
// The repository's TTL cache makes the read ~free; a changed setting
// applies within one cache window, no restart.
public interface IRuntimeSettings
{
    Task<RuntimeSettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

// Effective values for one ask. Only the keys something consumes at
// runtime today — see WellKnownSettings for what is deliberately absent.
public sealed record RuntimeSettingsSnapshot(
    double ConfidenceThreshold,
    int PerCallCostCeilingUsdCents,
    int MaxConversationTurns,
    // Retrieval tuning (PR retrieval-runtime-keys). Consumed at
    // searchCorpus call time by SearchCorpusTool.
    int RetrievalTopK,
    double RetrievalMinimumScore);

public sealed class RuntimeSettings : IRuntimeSettings
{
    private readonly IAdminSettingsRepository _repository;
    private readonly IOptions<AiFoundryOptions> _options;
    private readonly ILogger<RuntimeSettings> _logger;

    public RuntimeSettings(
        IAdminSettingsRepository repository,
        IOptions<AiFoundryOptions> options,
        ILogger<RuntimeSettings> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    public async Task<RuntimeSettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var defaults = _options.Value;
        var retrievalDefaults = new RetrievalOptions();

        var confidence = await ResolveAsync(
            WellKnownSettings.ConfidenceThreshold,
            defaults.ConfidenceThreshold,
            cancellationToken).ConfigureAwait(false);

        var ceiling = await ResolveAsync(
            WellKnownSettings.PerCallCostCeilingUsdCents,
            defaults.PerCallCostCeilingUsdCents,
            cancellationToken).ConfigureAwait(false);

        var turns = await ResolveAsync(
            WellKnownSettings.MaxConversationTurns,
            defaults.MaxConversationTurns,
            cancellationToken).ConfigureAwait(false);

        var topK = await ResolveAsync(
            WellKnownSettings.RetrievalTopK,
            retrievalDefaults.TopK,
            cancellationToken).ConfigureAwait(false);

        var minimumScore = await ResolveAsync(
            WellKnownSettings.RetrievalMinimumScore,
            retrievalDefaults.MinimumScore,
            cancellationToken).ConfigureAwait(false);

        return new RuntimeSettingsSnapshot(
            confidence,
            (int)ceiling,
            (int)turns,
            (int)topK,
            minimumScore);
    }

    // Stored override → default. A stored-but-unparsable value falls back
    // to the default VISIBLY (warning log per read; writes are validated
    // by WellKnownSettings.TryValidate, so this fires only on rows written
    // outside the page — Data Explorer edits, migration bugs). Repository
    // failures propagate: an ask should fail loudly rather than silently
    // run on defaults while the operator believes their override is live
    // (invariant #17 — no masking fallbacks). The repository's TTL cache
    // keeps Cosmos blips from translating into per-ask faults in practice.
    private async Task<double> ResolveAsync(string key, double defaultValue, CancellationToken ct)
    {
        var stored = await _repository.GetAsync(key, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return defaultValue;
        }

        if (double.TryParse(stored.Value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        _logger.LogWarning(
            "runtime_settings.unparsable — stored value for {Key} ('{Value}') is not numeric; using default {Default}. Fix or delete the row.",
            key, stored.Value, defaultValue);
        return defaultValue;
    }
}
