namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Per-change handler invoked by `CosmosChangeFeedHostedService<T>`
// for each document a Change Feed batch delivers.
//
// Implementations are responsible for the document-specific work
// (fetch related blob bytes, invoke the Application-layer pipeline,
// etc.). The hosted service handles batch iteration, lease ownership,
// and per-document failure routing (catch → dead-letter).
//
// `HandleAsync` MAY throw — the hosted service catches every
// non-cancellation exception, increments the dead-letter row's
// AttemptCount, and continues with the next document in the batch
// so a single poison document cannot stall the lease.
//
// `OperationCanceledException` carrying the host's stopping token
// MUST be allowed to propagate so the BackgroundService shuts down
// cleanly on host stop.
public interface ICosmosChangeFeedHandler<in T>
    where T : class
{
    Task HandleAsync(T change, CancellationToken cancellationToken);
}
