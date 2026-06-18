using System.Diagnostics;
using System.Net;
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
        CancellationToken cancellationToken)
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
}
