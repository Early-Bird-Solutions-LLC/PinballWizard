using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// Cosmos document for the <c>featured_machines</c> container per
/// <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 4</see>.
/// Represents a curated entry in the landing-page hero/featured strip;
/// the set is expected to be small (~6 entries) and is seeded via
/// <c>--seed-featured-machines</c> from <c>data/seeds/featured_machines.v1.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Id"/> equals <see cref="PartitionKey"/> (both are the slug),
/// so every read is a pure point-lookup with no secondary index. The
/// partition key path on the container is <c>/slug</c>. This mirrors the
/// <see cref="MachineTitleLookup"/> pattern (id == partition-key value) locked
/// in ADR-0025 § 4.
/// </para>
/// <para>
/// <see cref="DisplayOrder"/> is the only indexed field (ordering for the
/// landing strip) per ADR-0025 § 6 selective-indexing rule. The
/// <c>tagline</c> and <c>title</c> fields are display-only and are never
/// queried by the SDK on the user-facing path — excluding them from the
/// indexing policy saves RU on every seed upsert.
/// </para>
/// <para>
/// No TTL (<c>DefaultTtlSeconds = null</c>) — the curated list is static
/// between deploys and is replaced wholesale by re-running
/// <c>--seed-featured-machines</c>. There is no stale-row accumulation
/// problem for TTL to solve; auto-expiring rows would silently break
/// the landing page between seed runs.
/// </para>
/// </remarks>
public sealed class FeaturedMachineDocument : IEntity
{
    /// <summary>
    /// Document id — the machine slug (= partition key value). Cosmos
    /// partition path is <c>/slug</c>; the JSON property is named
    /// <c>slug</c> to match. Two equal arguments to a point-read is the
    /// intended contract, not a bug — mirrors <see cref="MachineTitleLookup"/>.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Partition key value — the machine slug. Equals <see cref="Id"/>
    /// by construction so point reads carry one string as both id and
    /// partition-key argument.
    /// </summary>
    [JsonPropertyName("slug")]
    public required string PartitionKey { get; init; }

    /// <summary>Human-readable title of the pinball machine.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// OPDB machine ID (e.g. <c>GRBN-MHVTP</c>) for cross-referencing the
    /// canonical machine catalog. Nullable: only set when the OPDB ID has
    /// been verified; never fabricated (CLAUDE.md showcase posture).
    /// </summary>
    [JsonPropertyName("opdb_id")]
    public string? OpdbId { get; init; }

    /// <summary>
    /// Sort key for rendering the featured strip in a deterministic order
    /// on the landing page. Lower values appear first. Must be positive.
    /// </summary>
    [JsonPropertyName("display_order")]
    public required int DisplayOrder { get; init; }

    /// <summary>
    /// Short marketing line displayed below the machine title on the landing
    /// page. Showcase-quality copy; prospects see this on first contact with
    /// the application.
    /// </summary>
    [JsonPropertyName("tagline")]
    public required string Tagline { get; init; }

    /// <summary>Cosmos system-managed _etag, populated on read.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}
