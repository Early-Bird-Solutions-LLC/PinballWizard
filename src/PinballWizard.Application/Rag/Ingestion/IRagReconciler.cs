namespace PinballWizard.Application.Rag.Ingestion;

// Phase 4 W3-2 reconcile-on-startup abstraction. Implementations sample
// the most-recently-recorded `rag_index_state` rows, verify AI Search
// holds matching chunks for each, and surface drift counts via
// `ReconciliationResult` and the `pinwiz.rag.changefeed_reconcile_*`
// telemetry instruments.
//
// Run posture: invoked once per worker startup when
// `RagIngestionOptions.ReconcileOnStartup=true`. Runs async after the
// change-feed processor starts so worker boot isn't blocked by the
// reconcile. The reconciler MAY take seconds to a minute on a curated
// subset (Cosmos sample query + per-document AI Search filter); on
// large indices the operator should expect proportionally longer.
//
// Failure posture: the reconciler should NOT throw to its caller —
// the hosted service catches and logs anyway, but the contract is that
// a reconcile exception is operationally a warning, not a fatal worker
// shutdown. Internal failures during sampling or verification are
// best-effort and surfaced via the drift counts (a partial sample is
// better than zero signal).
//
// The default Infrastructure implementation
// (`CosmosAiSearchRagReconciler`) reads `rag_index_state` directly from
// Cosmos and queries AI Search via filtered search.
public interface IRagReconciler
{
    Task<ReconciliationResult> ReconcileAsync(CancellationToken cancellationToken);
}

// Outcome of a single reconcile pass. `SampledCount` is the number of
// `rag_index_state` rows the reconciler actually inspected (may be less
// than the configured sample size if the container has fewer rows or
// the sampling query was partially cancelled). `MissingDriftCount` is
// rows where AI Search has zero chunks for the document_id —
// indicating a Phase 1 document that was indexed locally but the AI
// Search write was lost. `CountMismatchCount` is rows where AI Search
// has a different chunk count than the state row recorded —
// indicating partial-write drift (some chunks landed, others didn't).
//
// Drift counts are returned so the caller (typically the hosted
// service) can log a summary at the right level (info if all-zero,
// warning if any drift). The same counts are also emitted as
// `pinwiz.rag.changefeed_reconcile_drift_total` so operator dashboards
// see the trajectory across deploys.
public sealed record ReconciliationResult(
    int SampledCount,
    int MissingDriftCount,
    int CountMismatchCount,
    TimeSpan Duration);
