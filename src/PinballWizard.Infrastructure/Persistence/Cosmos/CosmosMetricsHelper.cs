using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Shared boundary-instrumentation helper per ADR-0025 § 8.
// Extracted from CosmosRepository<T> so non-repository Cosmos callers
// (CosmosBackedIndexState, CosmosBackedDeadLetterSink) can emit the same
// pinwiz.cosmos.ru_charge / pinwiz.cosmos.query_duration_ms instruments
// without inheriting from the repository base class.
internal static class CosmosMetricsHelper
{
    internal static async Task<TResult> ExecuteWithMetricsAsync<TResult>(
        string containerId,
        string operation,
        ILogger logger,
        Func<CancellationToken, Task<(TResult result, double requestCharge)>> action,
        CancellationToken cancellationToken,
        string? documentId = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (result, ru) = await action(cancellationToken).ConfigureAwait(false);
            Emit(containerId, operation, stopwatch.Elapsed, ru);
            return result;
        }
        catch (CosmosException ex)
        {
            Emit(containerId, operation, stopwatch.Elapsed, ex.RequestCharge);
            if (ex.StatusCode != HttpStatusCode.NotFound)
                LogFailureWithDiagnostics(containerId, operation, ex, logger);
            throw;
        }
        catch (JsonException ex)
        {
            // A malformed stored document — the Cosmos SDK's serializer
            // (SystemTextJsonCosmosSerializer.FromStream) threw because the
            // on-disk JSON doesn't match the target type. No RU is available
            // from the SDK at this point (the SDK charged for the read before
            // deserialization), so we emit a dedicated failure counter rather
            // than the RU/duration instruments. The error is logged with the
            // document id and container so operators can locate and remediate
            // the corrupt document without a manual log search.
            // Per invariant #17 / OBS-01: degrade visibly, never fabricate success.
            LogDeserializationFailure(containerId, operation, documentId, ex, logger);
            IncrementDeserFailedCounter(containerId, operation);
            throw;
        }
        // OperationCanceledException propagates — see CosmosRepository<T> for rationale.
    }

    private static void Emit(string containerId, string operation, TimeSpan duration, double ru)
    {
        var containerTag = new KeyValuePair<string, object?>("container", containerId);
        var operationTag = new KeyValuePair<string, object?>("operation", operation);
        PinballWizardTelemetry.CosmosRuCharge.Record(ru, containerTag, operationTag);
        PinballWizardTelemetry.CosmosQueryDurationMs.Record(duration.TotalMilliseconds, containerTag, operationTag);
    }

    private static void LogFailureWithDiagnostics(string containerId, string operation, CosmosException ex, ILogger logger)
    {
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["pinwiz.container"] = containerId,
            ["pinwiz.operation"] = operation,
            ["cosmos.status_code"] = (int)ex.StatusCode,
            ["cosmos.sub_status_code"] = ex.SubStatusCode,
            ["cosmos.activity_id"] = ex.ActivityId,
            ["cosmos.request_charge"] = ex.RequestCharge,
            ["cosmos.diagnostics"] = ex.Diagnostics?.ToString(),
        }))
        {
            logger.LogError(
                ex,
                "Cosmos {Operation} on container {Container} failed: {StatusCode}. Diagnostics captured in log scope.",
                operation, containerId, ex.StatusCode);
        }
    }

    private static void LogDeserializationFailure(
        string containerId,
        string operation,
        string? documentId,
        JsonException ex,
        ILogger logger)
    {
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["pinwiz.container"] = containerId,
            ["pinwiz.operation"] = operation,
            ["cosmos.document_id"] = documentId,
        }))
        {
            logger.LogError(
                ex,
                "Cosmos {Operation} on container {Container} failed to deserialize document '{DocumentId}': {Message}. Operator action: locate the document by id in the container and re-upsert it with the correct schema (pinwiz.cosmos.deser_failed_total incremented).",
                operation, containerId, documentId, ex.Message);
        }
    }

    private static void IncrementDeserFailedCounter(string containerId, string operation)
    {
        PinballWizardTelemetry.CosmosDeserializationFailed.Add(
            1,
            new KeyValuePair<string, object?>("container", containerId),
            new KeyValuePair<string, object?>("operation", operation));
    }
}
