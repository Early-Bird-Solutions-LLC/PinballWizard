using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Cosmos POCO for the `rag_dead_letters` container backing
// `IDeadLetterSink`. One row per document_id (NOT per failure
// event) — re-deliveries upsert with an incremented attempt count
// so the container row count stays bounded by document cardinality.
//
// `Id` = `RowIdPrefix + document_id` for deterministic addressing.
// Partition key path is `/document_id` (declared in CosmosOptions
// defaults).
//
// `ErrorClass` is intentionally unqualified type name (e.g.
// `RequestFailedException`) capped at 64 chars — full namespace
// adds noise without telemetry value. `ErrorMessage` is truncated at
// 1024 chars to keep the row size predictable; full traces flow to
// Log Analytics via OTel logs and don't need to live in Cosmos.
public sealed class DeadLetterDocument
{
    public const string RowIdPrefix = "dl_";

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("document_id")]
    public string DocumentId { get; init; } = string.Empty;

    [JsonPropertyName("attempt_count")]
    public int AttemptCount { get; init; }

    [JsonPropertyName("last_attempt_utc")]
    public DateTimeOffset LastAttemptUtc { get; init; }

    [JsonPropertyName("error_class")]
    public string ErrorClass { get; init; } = string.Empty;

    [JsonPropertyName("error_message")]
    public string ErrorMessage { get; init; } = string.Empty;

    [JsonPropertyName("change_lsn")]
    public string? ChangeLsn { get; init; }
}
