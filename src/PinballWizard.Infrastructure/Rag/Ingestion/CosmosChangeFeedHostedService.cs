using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Generic Cosmos Change Feed BackgroundService. Hosts one
// `Microsoft.Azure.Cosmos.ChangeFeedProcessor` per registered T,
// routes every delivered change to the registered
// `ICosmosChangeFeedHandler<T>`, and per-document-catches handler
// failures into `IDeadLetterSink`.
//
// Failure posture (per W3-2 design):
//   - Each document in a delivered batch runs in its own try/catch.
//     Handler exceptions are caught at the per-document boundary so a
//     single poison document cannot stall the lease.
//   - Before invoking the handler, the dead-letter sink is queried
//     for an existing record. If `AttemptCount >=
//     RagIngestionOptions.MaxFailuresPerDocument` the document is
//     skipped — the structurally-poison case is already known and
//     re-running won't change the outcome until an operator clears
//     the dead-letter row.
//   - On caught exception the dead-letter row is upserted with the
//     incremented attempt count so the next re-delivery sees the
//     updated count and eventually short-circuits.
//   - `OperationCanceledException` carrying the host's stopping token
//     is allowed to propagate so the BackgroundService shuts down
//     cleanly. Any other cancellation flavor is treated as a normal
//     handler failure (dead-letter + advance).
//
// Lease lifecycle: the processor owns lease checkpointing; we do not
// interact with `rag_leases` directly. On host stop the processor's
// StopAsync is awaited so in-flight batches complete cleanly and the
// next replica can pick up where this one left off.
public sealed class CosmosChangeFeedHostedService<T> : BackgroundService
    where T : class
{
    private readonly Container _sourceContainer;
    private readonly Container _leaseContainer;
    private readonly ICosmosChangeFeedHandler<T> _handler;
    private readonly IDeadLetterSink _deadLetterSink;
    private readonly Func<T, string> _documentIdSelector;
    private readonly Func<T, string?> _changeLsnSelector;
    private readonly RagIngestionOptions _ingestionOptions;
    private readonly CosmosChangeFeedHostedServiceOptions _changeFeedOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<CosmosChangeFeedHostedService<T>> _logger;

    private ChangeFeedProcessor? _processor;

    public CosmosChangeFeedHostedService(
        Container sourceContainer,
        Container leaseContainer,
        ICosmosChangeFeedHandler<T> handler,
        IDeadLetterSink deadLetterSink,
        Func<T, string> documentIdSelector,
        Func<T, string?> changeLsnSelector,
        IOptions<RagIngestionOptions> ingestionOptions,
        IOptions<CosmosChangeFeedHostedServiceOptions> changeFeedOptions,
        TimeProvider clock,
        ILogger<CosmosChangeFeedHostedService<T>> logger)
    {
        ArgumentNullException.ThrowIfNull(sourceContainer);
        ArgumentNullException.ThrowIfNull(leaseContainer);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(deadLetterSink);
        ArgumentNullException.ThrowIfNull(documentIdSelector);
        ArgumentNullException.ThrowIfNull(changeLsnSelector);
        ArgumentNullException.ThrowIfNull(ingestionOptions);
        ArgumentNullException.ThrowIfNull(changeFeedOptions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _sourceContainer = sourceContainer;
        _leaseContainer = leaseContainer;
        _handler = handler;
        _deadLetterSink = deadLetterSink;
        _documentIdSelector = documentIdSelector;
        _changeLsnSelector = changeLsnSelector;
        _ingestionOptions = ingestionOptions.Value;
        _changeFeedOptions = changeFeedOptions.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var instanceName = string.IsNullOrWhiteSpace(_changeFeedOptions.InstanceName)
            ? Environment.MachineName
            : _changeFeedOptions.InstanceName;

        var builder = _sourceContainer
            .GetChangeFeedProcessorBuilder<T>(
                _changeFeedOptions.ProcessorName,
                (changes, cancellationToken) =>
                    HandleChangesAsync(changes, cancellationToken))
            .WithInstanceName(instanceName)
            .WithLeaseContainer(_leaseContainer);

        if (_changeFeedOptions.StartFromBeginning)
        {
            builder = builder.WithStartTime(DateTime.MinValue.ToUniversalTime());
        }

        _processor = builder.Build();

        _logger.LogInformation(
            "RAG Change Feed processor starting: source={SourceContainer} leases={LeaseContainer} processor={ProcessorName} instance={InstanceName} startFromBeginning={StartFromBeginning} maxFailuresPerDocument={MaxFailuresPerDocument}.",
            _changeFeedOptions.SourceContainerName,
            _changeFeedOptions.LeaseContainerName,
            _changeFeedOptions.ProcessorName,
            instanceName,
            _changeFeedOptions.StartFromBeginning,
            _ingestionOptions.MaxFailuresPerDocument);

        // Reconciliation pass on startup: when enabled, the worker
        // samples N random `rag_index_state` rows and verifies AI
        // Search has matching chunks. The IMPLEMENTATION ships in
        // PR-C alongside the `pinwiz.rag.changefeed_reconcile_*`
        // instruments (per `RagIngestionOptions.ReconcileOnStartup`
        // docstring + the observability gap-closure rule —
        // instruments + emission ship together). PR-B reads the
        // option here so a deploy that sets `ReconcileOnStartup=true`
        // before PR-C lands gets a clear "feature pending" log line
        // rather than silent acceptance of a dead option.
        if (_ingestionOptions.ReconcileOnStartup)
        {
            _logger.LogInformation(
                "RAG Change Feed: ReconcileOnStartup is enabled; reconciliation pass will run once the implementation lands in W3-2 PR-C (with the `pinwiz.rag.changefeed_reconcile_*` instruments). No-op for now.");
        }

        await _processor.StartAsync().ConfigureAwait(false);

        try
        {
            // BackgroundService.ExecuteAsync conventionally blocks on
            // the host's stopping token — the ChangeFeedProcessor runs
            // independently in its own background loop and we just
            // wait until shutdown to call StopAsync.
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on host stop
        }
        finally
        {
            try
            {
                await _processor.StopAsync().ConfigureAwait(false);
                _logger.LogInformation(
                    "RAG Change Feed processor stopped: processor={ProcessorName} instance={InstanceName}.",
                    _changeFeedOptions.ProcessorName, instanceName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "RAG Change Feed processor StopAsync failed (continuing shutdown): processor={ProcessorName}.",
                    _changeFeedOptions.ProcessorName);
            }
        }
    }

    internal async Task HandleChangesAsync(
        IReadOnlyCollection<T> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var documentId = _documentIdSelector(change);
            if (string.IsNullOrWhiteSpace(documentId))
            {
                _logger.LogWarning(
                    "RAG Change Feed: skipping change with empty document id; payload type {PayloadType}.",
                    typeof(T).Name);
                continue;
            }

            // Short-circuit on already-dead-lettered documents so we
            // don't repeatedly re-invoke a handler that's structurally
            // failing. Operators clear the dead-letter row to retry.
            DeadLetterRecord? existingDeadLetter;
            try
            {
                existingDeadLetter = await _deadLetterSink
                    .GetAsync(documentId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The dead-letter LOOKUP itself failed (transient Cosmos
                // failure). Log and proceed to the handler — we'd rather
                // re-attempt the handler than silently skip a document.
                _logger.LogWarning(
                    ex,
                    "RAG Change Feed: dead-letter lookup failed for document={DocumentId}; proceeding to handler.",
                    documentId);
                existingDeadLetter = null;
            }

            if (existingDeadLetter is { } dl
                && dl.AttemptCount >= _ingestionOptions.MaxFailuresPerDocument)
            {
                _logger.LogDebug(
                    "RAG Change Feed: skipping document={DocumentId}; over retry budget (attempts={AttemptCount}).",
                    documentId, dl.AttemptCount);
                continue;
            }

            try
            {
                await _handler.HandleAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var newAttemptCount = (existingDeadLetter?.AttemptCount ?? 0) + 1;
                var record = new DeadLetterRecord(
                    DocumentId: documentId,
                    AttemptCount: newAttemptCount,
                    LastAttemptUtc: _clock.GetUtcNow(),
                    ErrorClass: TruncateClassName(ex.GetType().Name),
                    ErrorMessage: TruncateMessage(ex.Message),
                    ChangeLsn: _changeLsnSelector(change));

                try
                {
                    await _deadLetterSink
                        .UpsertAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception sinkEx)
                {
                    _logger.LogError(
                        sinkEx,
                        "RAG Change Feed: dead-letter UPSERT failed for document={DocumentId} (original error: {OriginalError}). Batch advances regardless.",
                        documentId, ex.Message);
                }
            }
        }
    }

    private const int ErrorClassMaxLen = 64;
    private const int ErrorMessageMaxLen = 1024;

    private static string TruncateClassName(string name) =>
        name.Length <= ErrorClassMaxLen ? name : name[..ErrorClassMaxLen];

    private static string TruncateMessage(string message) =>
        message.Length <= ErrorMessageMaxLen ? message : message[..ErrorMessageMaxLen];
}
