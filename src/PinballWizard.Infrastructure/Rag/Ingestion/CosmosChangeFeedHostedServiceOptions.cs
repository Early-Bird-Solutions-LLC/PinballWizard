using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Configuration for the W3-2 Cosmos Change Feed hosted service. One
// instance is bound per change-feed consumer registered in DI; today
// the W3-2 worker registers a single instance against
// `scraped_documents`.
//
// `SourceContainerName`, `LeaseContainerName`, and `ProcessorName`
// MUST match the values the Bicep KEDA Cosmos scaler is configured
// for (see `infra/modules/shared.bicep` § RAG Indexer Container App
// scale rule). Drift is silent — the worker would acquire a different
// lease set than the scaler watches and KEDA would never scale up.
//
// `InstanceName` defaults to the machine name (the hosted service
// reads `Environment.MachineName` when this is null) so each
// Container App replica gets a unique lease-owner name. Pin this to
// a fixed string ONLY for tests.
public sealed class CosmosChangeFeedHostedServiceOptions
{
    public const string SectionName = "Rag:ChangeFeed";

    [Required]
    public string SourceContainerName { get; init; } = "scraped_documents";

    [Required]
    public string LeaseContainerName { get; init; } = "rag_leases";

    [Required]
    public string ProcessorName { get; init; } = "rag-indexer";

    public string? InstanceName { get; init; }

    // Whether to start the change-feed processor from the beginning
    // of the source container's history on first lease acquisition
    // (true) or from the moment the processor first starts (false).
    // Default true matches Phase 4's expectation: a fresh deploy
    // should backfill the curated subset's existing scraped documents,
    // not just process new ones. Subsequent processor restarts always
    // resume from the last checkpoint regardless of this setting —
    // this only affects the first-ever lease grant.
    public bool StartFromBeginning { get; init; } = true;

    // How often the hosted service polls Cosmos's `ChangeFeedEstimator`
    // to refresh the cached lease-lag value behind the
    // `pinwiz.rag.changefeed_lease_lag` ObservableGauge. Default 30s
    // matches the dashboard refresh granularity: faster polling burns
    // RU on the lease container without surfacing actionable signal at
    // operator timescale; slower polling makes the gauge feel stale.
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan LeaseLagPollInterval { get; init; } = TimeSpan.FromSeconds(30);
}
