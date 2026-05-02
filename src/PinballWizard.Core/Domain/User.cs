using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// An end-user of the pinwiz.ai platform, identified by their Entra
/// External ID object ID (<c>oid</c>). Per ADR 0009 social-login federated
/// identities (Google / Apple / Discord) all map back to a single Entra
/// external user record; this entity is keyed on that record.
/// </summary>
/// <remarks>
/// Sketch only — Phase 5 work fleshes this out when Digital Passport
/// features ship. Captured here so the partition-key strategy locks
/// (per-user partition for all user-tied data) and the schema vocabulary
/// is consistent across the catalog.
/// </remarks>
public sealed class User : IEntity
{
    /// <summary>Entra External ID OID — also the Cosmos document id and partition key.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Partition key — same value as <see cref="Id"/> (per-user partition).</summary>
    [JsonPropertyName("userId")]
    public string PartitionKey => Id;

    /// <summary>Display name as set in Entra.</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; set; }

    /// <summary>Federated identity provider key (<c>google</c>, <c>apple</c>, <c>discord</c>, etc.).</summary>
    [JsonPropertyName("identityProvider")]
    public required string IdentityProvider { get; init; }

    /// <summary>When the user first signed in.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Most recent sign-in timestamp.</summary>
    [JsonPropertyName("lastLoginAt")]
    public DateTimeOffset LastLoginAt { get; set; }

    /// <summary>Cosmos system-managed _etag.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}
