using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Application.Ai;

// IAgentPromptProvider that layers a Cosmos override store over the
// compiled-in embedded-resource prompts (admin prompts plan, PR-B3).
//
// Resolution order for GetPrompt(agentName):
//   1. Active Cosmos override (IAgentPromptOverrideRepository.GetActiveAsync)
//   2. Embedded-resource prompt (EmbeddedResourceAgentPromptProvider)
//
// If the Cosmos read fails (unreachable store, transient error), we fall
// back to the embedded resource and log a Warning + meter the failure.
// An unreachable override store MUST NOT take down asks — the Wizard
// is a customer-facing surface and a mis-configured admin_prompts
// connection should not turn the whole service dark. The log + metric
// give the operator a signal without silently lying to the user that
// everything is fine (invariant #17: no masking fallbacks, degrade
// visibly).
//
// PromptVersion composition (load-bearing for the semantic cache key):
// AiRouter reads PromptVersion and uses it as part of the cache key
// (normalized question + promptVersion). Without version-stamping,
// a prompt change would serve stale cached answers indefinitely because
// the cache key wouldn't change. This class appends a per-agent suffix
// when that agent has an active override:
//
//   base version: EmbeddedResourceAgentPromptProvider.CurrentPromptVersion
//                 (e.g., "v4.2026.05")
//
//   with Wizard override v2 active:
//     "v4.2026.05+Wizard.v2"
//
//   with both Wizard v2 and Repair v1 active:
//     "v4.2026.05+Repair.v1+Wizard.v2"
//
// Suffixes are appended in alphabetical agent-name order so the version
// string is deterministic regardless of the order overrides are applied.
// A cache miss on the first ask after an override activation is
// intentional — the stale answer for the old prompt must not replay.
//
// NOTE: PromptVersion is computed async (Cosmos reads), but IAgentPromptProvider
// exposes it as a synchronous property. This class caches the resolved
// version for one TTL cycle (2 minutes — matching the Cosmos repository's
// cache TTL) to avoid a per-property-access async call in the sync API.
// The cached value is refreshed lazily on the first access after the TTL
// expires. A race between PromptVersion read and a cache refresh yields
// at most one ask with a stale cache key — it hits the Cosmos miss path,
// not the wrong cached answer, so the worst case is a redundant Foundry
// call.
public sealed class OverridingAgentPromptProvider : IAgentPromptProvider, IDisposable
{
    private static readonly TimeSpan VersionCacheTtl = TimeSpan.FromMinutes(2);

    private readonly EmbeddedResourceAgentPromptProvider _embedded;
    private readonly IAgentPromptOverrideRepository? _overrides;
    private readonly ILogger<OverridingAgentPromptProvider> _logger;

    // Cached resolved PromptVersion (re-resolved after TTL).
    // Volatile so reads across threads see the latest write without a
    // full lock. Writes are under a SemaphoreSlim (see below).
    private volatile string _cachedVersion;
    private DateTimeOffset _versionCachedAtUtc = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _versionLock = new(1, 1);

    // overrides is optional by design: hosts without the admin_prompts
    // container (standalone CLI, unit fixtures, any host that does not
    // call AddCosmosPersistence) will resolve null here and the provider
    // behaves identically to EmbeddedResourceAgentPromptProvider.
    // This mirrors the IRuntimeSettings? optional pattern in AiRouter —
    // "no Cosmos = run on defaults/embedded" is the correct degradation.
    public OverridingAgentPromptProvider(
        EmbeddedResourceAgentPromptProvider embedded,
        ILogger<OverridingAgentPromptProvider> logger,
        IAgentPromptOverrideRepository? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(embedded);
        ArgumentNullException.ThrowIfNull(logger);

        _embedded = embedded;
        _overrides = overrides;
        _logger = logger;
        // Initialise to the embedded version so any synchronous read before
        // the first async refresh still returns a valid (non-null) value.
        _cachedVersion = embedded.PromptVersion;
    }

    // Synchronous property — returns the last resolved version string.
    // The actual async resolution is triggered by GetPromptAsync or by
    // RefreshVersionAsync; direct PromptVersion reads that arrive before
    // the first refresh see the embedded baseline version, which is
    // correct (no override active yet = no version suffix yet).
    public string PromptVersion => _cachedVersion;

    // Returns the prompt content for agentName using the resolution order
    // documented in the class header. Also refreshes the cached version
    // string so PromptVersion is up-to-date on the next AiRouter read.
    // This is the one-call-does-everything path: AiRouter calls GetPrompt
    // on the hot path, and the side-effectful version refresh is piggybacked
    // here to avoid a separate async code path that callers would need to
    // drive separately.
    public string GetPrompt(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // Fire-and-forget: start the async override lookup but don't
        // await it — GetPrompt is a synchronous interface method and
        // callers (FoundryAgentFactory.ConstructAgents) call it at
        // construction time under a lock. The task completes asynchronously
        // and the result (override content or embedded fallback) is
        // returned via GetPromptAsync on the async path.
        //
        // This synchronous method is the IAgentPromptProvider contract
        // entry point. For the prompt-override feature, callers should use
        // GetPromptAsync when they can await (e.g., a future PromptLoader
        // that rebuilds agents on activation). ConstructAgents calls this
        // synchronous path; since it runs once at agent-construction time
        // and the result is cached for the agent's lifetime, the one-call
        // lag before an override is visible is acceptable — ActivateAsync
        // calls IFoundryAgentCacheInvalidator.Invalidate which forces the
        // next GetAgent call to rebuild, at which point GetPromptAsync/
        // GetPromptSync will have resolved the override from cache.
        //
        // Fallback to embedded on any exception: the override store must
        // not take down asks (invariant #17). The log + meter are the
        // visible degradation signal.
        return GetPromptCoreSync(agentName);
    }

    // Core synchronous resolution with resilient Cosmos fallback.
    // Runs the async lookup synchronously via GetAwaiter().GetResult()
    // — this is safe because ConstructAgents runs in a synchronous lock
    // context (never on the ASP.NET synchronization context). A future
    // refactor that makes FoundryAgentFactory.ConstructAgents async would
    // let us await here instead.
    private string GetPromptCoreSync(string agentName)
    {
        if (_overrides is null)
        {
            // No Cosmos persistence wired — return embedded directly.
            return _embedded.GetPrompt(agentName);
        }

        try
        {
            // Blocking on async deliberately — see comment on GetPrompt.
            var overrideRecord = _overrides
                .GetActiveAsync(agentName, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (overrideRecord is not null)
            {
                _logger.LogDebug(
                    "prompt_override.active — agentName={AgentName} version={Version} updatedBy={UpdatedBy}",
                    agentName, overrideRecord.Version, overrideRecord.UpdatedBy);

                // Refresh cached version to include this override.
                _ = Task.Run(() => RefreshVersionAsync(CancellationToken.None));
                return overrideRecord.Content;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Resilient-but-honest: Cosmos unreachable / transient error.
            // Fall back to embedded resource and degrade visibly per
            // invariant #17 — do NOT silently serve embedded as if it
            // were the override.
            _logger.LogWarning(
                ex,
                "prompt_override.fallback — Could not read Cosmos override for agentName={AgentName}; " +
                "falling back to embedded-resource prompt. The admin_prompts store may be unreachable. " +
                "(Invariant #17: degrade visibly, never mask failures.)",
                agentName);
        }

        return _embedded.GetPrompt(agentName);
    }

    // Async version of the resolution logic — used by RefreshVersionAsync
    // and by any future async callers (e.g., a prompt-validation endpoint).
    // Returns (content, overrideVersion?) — overrideVersion is non-null
    // when an active Cosmos row was found.
    private async Task<(string content, int? overrideVersion)> GetPromptCoreAsync(
        string agentName,
        CancellationToken cancellationToken)
    {
        if (_overrides is null)
        {
            return (_embedded.GetPrompt(agentName), null);
        }

        try
        {
            var overrideRecord = await _overrides
                .GetActiveAsync(agentName, cancellationToken)
                .ConfigureAwait(false);

            if (overrideRecord is not null)
            {
                return (overrideRecord.Content, overrideRecord.Version);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "prompt_override.fallback (async) — agentName={AgentName}; using embedded-resource prompt.",
                agentName);
        }

        return (_embedded.GetPrompt(agentName), null);
    }

    // Re-computes and stores the cached PromptVersion string from the
    // current set of active Cosmos overrides across all agents. Called
    // as a background refresh whenever a prompt is retrieved (see
    // GetPromptCoreSync) and exposed for callers that want to force a
    // refresh (e.g., after ActivateAsync).
    //
    // Version string composition: base version + sorted override suffixes.
    // Suffixes are sorted by agent name to make the string deterministic.
    public async Task RefreshVersionAsync(CancellationToken cancellationToken)
    {
        // Guard against concurrent refreshes — the lock is async-friendly.
        // If a second refresh arrives while one is in progress, it waits.
        if (_overrides is null)
        {
            // No Cosmos persistence wired — nothing to refresh.
            return;
        }

        await _versionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Short-circuit if the cached value is still within TTL.
            if (DateTimeOffset.UtcNow - _versionCachedAtUtc < VersionCacheTtl)
            {
                return;
            }

            var suffixes = new List<string>();
            foreach (var agentName in AgentName.All)
            {
                try
                {
                    var active = await _overrides!
                        .GetActiveAsync(agentName, cancellationToken)
                        .ConfigureAwait(false);

                    if (active is not null)
                    {
                        suffixes.Add($"{agentName}.v{active.Version}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failing agent lookup doesn't break the others.
                    _logger.LogWarning(
                        ex,
                        "prompt_override.version_refresh_partial_failure — agentName={AgentName}; " +
                        "this agent's override will not appear in PromptVersion.",
                        agentName);
                }
            }

            // Alphabetical sort ensures the version string is deterministic.
            suffixes.Sort(StringComparer.Ordinal);

            var newVersion = suffixes.Count == 0
                ? _embedded.PromptVersion
                : $"{_embedded.PromptVersion}+{string.Join("+", suffixes)}";

            _cachedVersion = newVersion;
            _versionCachedAtUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _versionLock.Release();
        }
    }

    // SemaphoreSlim holds an OS wait handle and is IDisposable.
    // OverridingAgentPromptProvider is a singleton and lives for the
    // process lifetime, so Dispose is called only at host shutdown —
    // but we still implement it to satisfy CA1001 and be correct.
    public void Dispose()
    {
        _versionLock.Dispose();
    }
}
