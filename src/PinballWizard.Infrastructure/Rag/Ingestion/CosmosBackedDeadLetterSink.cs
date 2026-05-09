using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Default `IDeadLetterSink` impl backed by the `rag_dead_letters`
// Cosmos container. One row per document_id; deterministic id
// `dl_<document_id>` so the lookup is a point-read.
//
// `GetAsync` returns null on 404 — distinguishes "never failed"
// from "failed but cleared" (the latter requires the operator to
// delete the row, which causes the next failure to start at
// AttemptCount=1 again — exactly the recovery semantics we want).
//
// `UpsertAsync` is idempotent on the deterministic id so concurrent
// re-deliveries of the same document converge. Callers compute the
// new AttemptCount themselves to keep the sink free of read-modify-
// write semantics.
public sealed class CosmosBackedDeadLetterSink : IDeadLetterSink
{
    private readonly Container _container;
    private readonly ILogger<CosmosBackedDeadLetterSink> _logger;

    public CosmosBackedDeadLetterSink(
        Container container,
        ILogger<CosmosBackedDeadLetterSink> logger)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(logger);
        _container = container;
        _logger = logger;
    }

    public async Task<DeadLetterRecord?> GetAsync(
        string documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        try
        {
            var response = await _container.ReadItemAsync<DeadLetterDocument>(
                DeadLetterDocument.RowIdPrefix + documentId,
                new PartitionKey(documentId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var d = response.Resource;
            return new DeadLetterRecord(
                DocumentId: d.DocumentId,
                AttemptCount: d.AttemptCount,
                LastAttemptUtc: d.LastAttemptUtc,
                ErrorClass: d.ErrorClass,
                ErrorMessage: d.ErrorMessage,
                ChangeLsn: d.ChangeLsn);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpsertAsync(
        DeadLetterRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.DocumentId);

        var doc = new DeadLetterDocument
        {
            Id = DeadLetterDocument.RowIdPrefix + record.DocumentId,
            DocumentId = record.DocumentId,
            AttemptCount = record.AttemptCount,
            LastAttemptUtc = record.LastAttemptUtc,
            ErrorClass = record.ErrorClass,
            ErrorMessage = record.ErrorMessage,
            ChangeLsn = record.ChangeLsn,
        };

        await _container.UpsertItemAsync(
            doc,
            new PartitionKey(record.DocumentId),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "RAG dead-letter: document={DocumentId} attempt={AttemptCount} error={ErrorClass} ({ErrorMessage}).",
            record.DocumentId, record.AttemptCount, record.ErrorClass, record.ErrorMessage);
    }
}
