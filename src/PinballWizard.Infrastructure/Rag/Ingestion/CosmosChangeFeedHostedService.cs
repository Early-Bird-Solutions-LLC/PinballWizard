using System.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Observability;
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
    private readonly IRagReconciler? _reconciler;
    private readonly Func<T, string> _documentIdSelector;
    private readonly Func<T, string?> _changeLsnSelector;
    private readonly RagIngestionOptions _ingestionOptions;
    private readonly CosmosChangeFeedHostedServiceOptions _changeFeedOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<CosmosChangeFeedHostedService<T>> _logger;

    private ChangeFeedProcessor? _processor;
    private ChangeFeedProcessor? _estimator;

    // `reconciler` is OPTIONAL — null means the reconcile-on-startup
    // pass is unavailable in this host (typical for unit tests + the
    // sibling `machines` change-feed consumer that doesn't need a
    // reconciler). When `RagIngestionOptions.ReconcileOnStartup=true`
    // and the reconciler is null, ExecuteAsync logs a warning and
    // skips — better than crashing the worker on a config combination
    // an integration test might produce.
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
        ILogger<CosmosChangeFeedHostedService<T>> logger,
        IRagReconciler? reconciler = null)
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
        _reconciler = reconciler;
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

        await _processor.StartAsync().ConfigureAwait(false);

        // Reconcile-on-startup. Runs ASYNC after the change-feed
        // processor starts so worker boot isn't blocked by the
        // reconcile (which can take seconds-to-a-minute for a typical
        // sample size). Result is logged + emitted as
        // `pinwiz.rag.changefeed_reconcile_*` instruments by the
        // reconciler itself; no return-value handling needed here.
        if (_ingestionOptions.ReconcileOnStartup)
        {
            if (_reconciler is null)
            {
                _logger.LogWarning(
                    "RAG Change Feed: ReconcileOnStartup is enabled but no IRagReconciler is registered; skipping. Wire `AddCosmosChangeFeedRagIngestion` (or supply a reconciler explicitly) to enable.");
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Worker shutting down mid-reconcile; expected.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "RAG Change Feed: reconcile-on-startup pass threw to the hosted service. Worker continues serving the change feed.");
                    }
                }, stoppingToken);
            }
        }

        // Lease-lag estimator. Cosmos's ChangeFeedEstimator builds a
        // *secondary* ChangeFeedProcessor that owns its own lease-state
        // observation loop and fires the ChangesEstimationHandler with
        // the cross-lease total estimated lag (delegate signature:
        // `(long estimatedLag, CancellationToken) => Task`). We push
        // that value into PinballWizardTelemetry's static cache; the
        // `pinwiz.rag.changefeed_lease_lag` ObservableGauge callback
        // reads the cache (sync, no I/O on the export thread).
        //
        // Failure posture: estimator startup faults are logged at
        // warning; the gauge stays at its last known value (initial 0).
        // Per-poll handler exceptions are caught inside the handler so
        // the SDK's poll loop keeps running.
        try
        {
            _estimator = _sourceContainer
                .GetChangeFeedEstimatorBuilder(
                    _changeFeedOptions.ProcessorName,
                    LeaseLagEstimationHandler,
                    _changeFeedOptions.LeaseLagPollInterval)
                .WithLeaseContainer(_leaseContainer)
                .Build();
            await _estimator.StartAsync().ConfigureAwait(false);
            _logger.LogInformation(
                "RAG Change Feed lease-lag estimator started: pollInterval={PollInterval}.",
                _changeFeedOptions.LeaseLagPollInterval);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RAG Change Feed: lease-lag estimator startup failed; `pinwiz.rag.changefeed_lease_lag` gauge will report 0 until a future deploy fixes the configuration. Worker continues serving the change feed.");
        }

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

            if (_estimator is not null)
            {
                try
                {
                    await _estimator.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "RAG Change Feed lease-lag estimator StopAsync failed (continuing shutdown).");
                }
            }
        }
    }

    // ChangesEstimationHandler — fires periodically (per
    // CosmosChangeFeedHostedServiceOptions.LeaseLagPollInterval) with
    // the cross-lease total estimated lag. Pushes the value into
    // PinballWizardTelemetry's static cache so the ObservableGauge
    // can read it without I/O. Catches exceptions internally so a
    // single bad sample doesn't tear down the SDK's poll loop.
    private Task LeaseLagEstimationHandler(long estimatedLag, CancellationToken cancellationToken)
    {
        try
        {
            PinballWizardTelemetry.RecordChangefeedLeaseLag(estimatedLag);
        }
        catch (Exception ex)
        {
            // Should never happen — the cache update is just an
            // Interlocked.Exchange — but defending the SDK's poll loop
            // is cheap and the alternative (estimator silently dying
            // on a transient telemetry failure) is operationally bad.
            _logger.LogWarning(
                ex,
                "RAG Change Feed: lease-lag cache update failed; gauge retains previous value.");
        }
        return Task.CompletedTask;
    }

    internal async Task HandleChangesAsync(
        IReadOnlyCollection<T> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        // Stopwatch wraps the full batch — operators chart p50/p95 of
        // `pinwiz.rag.changefeed_batch_duration_ms` to detect ingestion
        // slowdowns before they manifest as lease-lag spikes. Tagged
        // with a coarse batch-size bucket so latency growth can be
        // attributed to batch-size shifts vs. per-document slowdown
        // without exploding cardinality on raw counts. Emitted in
        // `finally` so cancellation + transport failures both surface
        // a duration sample (failures still cost wall-clock the operator
        // paid for).
        var stopwatch = Stopwatch.StartNew();
        var batchSizeTag = new KeyValuePair<string, object?>(
            "batch_size_bucket",
            ClassifyBatchSize(changes.Count));

        try
        {
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var documentId = _documentIdSelector(change);
                if (string.IsNullOrWhiteSpace(documentId))
                {
                    _logger.LogWarning(
                        "RAG Change Feed: skipping change with empty document id; payload type {PayloadType}.",
                        typeof(T).Name);
                    PinballWizardTelemetry.RagChangefeedShortCircuitTotal.Add(
                        1, new KeyValuePair<string, object?>("reason", "empty_document_id"));
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
                    PinballWizardTelemetry.RagChangefeedShortCircuitTotal.Add(
                        1, new KeyValuePair<string, object?>("reason", "over_budget"));
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
                    var errorClass = TruncateClassName(ex.GetType().Name);
                    var record = new DeadLetterRecord(
                        DocumentId: documentId,
                        AttemptCount: newAttemptCount,
                        LastAttemptUtc: _clock.GetUtcNow(),
                        ErrorClass: errorClass,
                        ErrorMessage: TruncateMessage(ex.Message),
                        ChangeLsn: _changeLsnSelector(change));

                    try
                    {
                        await _deadLetterSink
                            .UpsertAsync(record, cancellationToken).ConfigureAwait(false);
                        // Increment the dead-letter counter only AFTER the
                        // sink upsert succeeded — a failed upsert means the
                        // dead-letter row didn't actually land, so the
                        // operator dashboard shouldn't think it did. The
                        // sink-failure path below logs separately.
                        PinballWizardTelemetry.RagChangefeedDeadLetterTotal.Add(
                            1, new KeyValuePair<string, object?>("error_class", errorClass));
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
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.RagChangefeedBatchDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                batchSizeTag);
        }
    }

    // Coarse batch-size bucketing for the `batch_size_bucket` tag on
    // `pinwiz.rag.changefeed_batch_duration_ms`. Buckets chosen for
    // operational signal at curated-subset scale: most batches are
    // 1-5 documents (steady-state), occasional ramps hit 11-50, larger
    // is unusual and worth surfacing.
    internal static string ClassifyBatchSize(int count) => count switch
    {
        <= 0 => "0",
        1 => "1",
        <= 10 => "2-10",
        <= 50 => "11-50",
        _ => "51+",
    };

    private const int ErrorClassMaxLen = 64;
    private const int ErrorMessageMaxLen = 1024;

    private static string TruncateClassName(string name) =>
        name.Length <= ErrorClassMaxLen ? name : name[..ErrorClassMaxLen];

    private static string TruncateMessage(string message) =>
        message.Length <= ErrorMessageMaxLen ? message : message[..ErrorMessageMaxLen];
}
