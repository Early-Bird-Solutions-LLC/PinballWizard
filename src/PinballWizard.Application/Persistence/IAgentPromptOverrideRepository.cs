namespace PinballWizard.Application.Persistence;

// Per-agent prompt override store (admin prompts plan, PR-B3).
//
// Overrides layer OVER the embedded-resource prompts in
// EmbeddedResourceAgentPromptProvider: an active Cosmos row for a given
// agent_name is served instead of the compiled-in .md content; deleting
// or deactivating the row (DeactivateAsync) reverts to the embedded
// default. OverridingAgentPromptProvider (Application.Ai) is the read-
// side facade that merges the two sources.
//
// Version model: SaveNewVersionAsync always writes a NEW row (auto-
// incremented version number) in INACTIVE state — the admin page
// previews it before activating. ActivateAsync atomically promotes a
// chosen version and demotes all others in the same partition (one-
// active-per-agent invariant enforced by the repository, not the
// caller). This means the audit trail is complete: every version ever
// saved is queryable via GetVersionsAsync and can be re-activated.
//
// Implementations should TTL-cache GetActiveAsync (2 minutes, evict on
// Activate/Deactivate/Save) — the prompt provider calls it on every
// ask; without the cache each answer would cost a Cosmos read. Negative
// entries (no override stored) must also be cached so a default-running
// install doesn't issue N reads per ask where N = number of agents.
public interface IAgentPromptOverrideRepository
{
    // Returns the active override for agentName, or null if none is
    // active (caller falls back to the embedded-resource default).
    Task<AgentPromptOverride?> GetActiveAsync(string agentName, CancellationToken cancellationToken);

    // All stored versions for agentName (for the admin page's history
    // view). Returns an empty list when no overrides have ever been
    // saved for that agent.
    Task<IReadOnlyList<AgentPromptOverride>> GetVersionsAsync(string agentName, CancellationToken cancellationToken);

    // Writes a new version row (INACTIVE) for agentName. The version
    // number is auto-incremented from the highest version currently
    // stored for this agent (0 if none). updatedBy is the authenticated
    // admin's display name — the audit gap this store exists to close.
    // Returns the newly created record so the admin page can show it.
    Task<AgentPromptOverride> SaveNewVersionAsync(
        string agentName,
        string content,
        string updatedBy,
        CancellationToken cancellationToken);

    // Promotes version to ACTIVE and atomically demotes all other
    // versions for agentName. The one-active-per-agent invariant is
    // enforced here, not by callers. Also evicts the agent's TTL-cache
    // entry so the next GetActiveAsync reads the new truth.
    Task ActivateAsync(string agentName, int version, CancellationToken cancellationToken);

    // Deactivates all versions for agentName — effectively reverts to
    // the embedded-resource default. Idempotent: calling on an already-
    // inactive agent is a no-op. Evicts the TTL-cache entry so the next
    // GetActiveAsync sees null immediately (no stale override window).
    Task DeactivateAsync(string agentName, CancellationToken cancellationToken);
}

// One stored prompt version. IsActive==true means the override is live;
// the embedding default is used when no active row exists for an agent.
public sealed record AgentPromptOverride(
    string AgentName,
    int Version,
    string Content,
    bool IsActive,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);
