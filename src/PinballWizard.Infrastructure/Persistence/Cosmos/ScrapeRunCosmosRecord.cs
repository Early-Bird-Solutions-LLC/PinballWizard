using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Cosmos POCO for the scrape_runs container. Partition key path is /source_id;
// id is the deterministic "{source_id}_{run_at}" derived in the repository. Snake_case
// [JsonPropertyName] decorations match the container field names.
internal sealed class ScrapeRunCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    // IEntity.PartitionKey — partition key path is /source_id.
    [JsonPropertyName("source_id")]
    public required string PartitionKey { get; set; }

    [JsonPropertyName("run_at")]
    public DateTimeOffset RunAt { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; set; }

    [JsonPropertyName("documents_discovered")]
    public int DocumentsDiscovered { get; set; }

    [JsonPropertyName("documents_new")]
    public int DocumentsNew { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}
