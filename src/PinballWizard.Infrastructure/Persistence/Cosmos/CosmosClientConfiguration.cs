using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Serialization + write-behavior settings that MUST be identical on every
/// <see cref="CosmosClient"/> construction path:
/// <list type="bullet">
///   <item>the client .NET Aspire's <c>AddAzureCosmosClient("cosmos")</c> builds
///   for the local preview emulator (and for deployed Cosmos when an Aspire
///   connection string is present), and</item>
///   <item>the Managed-Identity fallback client built by the registration
///   factory in <see cref="ServiceCollectionExtensions.AddCosmosPersistence"/>.</item>
/// </list>
/// Applied via the <c>configureClientOptions</c> argument to
/// <c>AddAzureCosmosClient</c> for the Aspire client (see
/// <see cref="CosmosHostRegistration"/>), and directly by the fallback factory.
/// The load-bearing setting is <see cref="CosmosClientOptions.Serializer"/>. The
/// domain documents annotate their JSON shape with System.Text.Json
/// <c>[JsonPropertyName]</c> attributes (e.g. <c>partitionKey</c>, <c>id</c>).
/// The Cosmos SDK's default serializer ignores those attributes, so a document
/// written through a default-serializer client lands its partition key under the
/// wrong name (<c>PartitionKey</c>) and the gateway rejects the write with
/// <c>400 BadRequest</c> (RU=0). Applying <see cref="SystemTextJsonCosmosSerializer"/>
/// on both paths keeps local-emulator writes byte-identical to live writes.
/// Transport settings (<see cref="CosmosClientOptions.ConnectionMode"/>,
/// <c>LimitToEndpoint</c>, <c>ApplicationPreferredRegions</c>) are intentionally
/// NOT set here — they are path-specific (Aspire configures them for the
/// emulator; the fallback sets them per environment).
/// </summary>
internal static class CosmosClientConfiguration
{
    /// <summary>
    /// The shared System.Text.Json options. camelCase naming + null-omission
    /// match the documents' <c>[JsonPropertyName]</c> attributes and the
    /// container partition-key paths (e.g. <c>/partitionKey</c>, <c>/slug</c>).
    /// </summary>
    internal static JsonSerializerOptions BuildJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Applies the serializer + write-behavior options (ADR-0025 § 2) that every
    /// CosmosClient in the app must share. Does not touch transport options.
    /// </summary>
    internal static void ApplySharedOptions(CosmosClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Serializer = new SystemTextJsonCosmosSerializer(BuildJsonOptions());
        // Per ADR-0025 § 2 — no write round-trip for the response body.
        options.EnableContentResponseOnWrite = false;
        // Per ADR-0025 § 2 — auto-batch same-partition concurrent ops.
        options.AllowBulkExecution = true;
        options.ConsistencyLevel = ConsistencyLevel.Session;
    }
}
