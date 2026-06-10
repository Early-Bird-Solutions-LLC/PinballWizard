using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// A physical pinball machine, identified by its OPDB canonical ID.
/// </summary>
/// <remarks>
/// Machines are the spine of the catalog — every score, strategy, dream
/// game, and document references a Machine through its OPDB ID. Per ADR
/// 0007 the manufacturer-keyed partition strategy lets per-manufacturer
/// scrapers operate within their own partition without cross-partition
/// contention.
/// <para>
/// The <see cref="Id"/> is the OPDB ID itself (for example
/// <c>GRBN-MQR4P</c> for Stranger Things Pro). Manufacturer-specific
/// slugs (the Stern URL slug, JJP product code, etc.) live in
/// <see cref="ManufacturerSlugs"/> as an alternate-key dictionary.
/// </para>
/// </remarks>
public sealed class Machine : IEntity
{
    /// <summary>OPDB canonical ID — also the Cosmos document id.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Manufacturer key — partition key. Lower-case (<c>stern</c>, <c>jjp</c>, <c>americanpinball</c>, etc.).</summary>
    [JsonPropertyName("manufacturer")]
    public required string PartitionKey { get; init; }

    /// <summary>Human-readable manufacturer name (e.g., "Stern Pinball").</summary>
    [JsonPropertyName("manufacturerDisplayName")]
    public required string ManufacturerDisplayName { get; init; }

    /// <summary>Machine title (e.g., "Stranger Things").</summary>
    [JsonPropertyName("title")]
    public required string Title { get; set; }

    /// <summary>
    /// OPDB group segment — the leading part of the OPDB ID before the
    /// first hyphen (e.g. <c>GweeP</c> for "Godzilla (Pro)"
    /// <c>GweeP-MW95j</c>). A <em>relational</em> key for discovering
    /// sibling machines of the same title (different editions / a
    /// cross-year reissue), NOT a merge key — every base record stays a
    /// distinct Machine per
    /// <see href="../../../docs/adr/0029-version-aware-answering.md">ADR-0029</see>.
    /// Null when the OPDB ID has no derivable segment (defensive only —
    /// well-formed OPDB IDs always do).
    /// </summary>
    [JsonPropertyName("groupId")]
    public string? GroupId { get; set; }

    /// <summary>Year the machine was released. Null if unknown.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>Designer(s) credited by OPDB.</summary>
    [JsonPropertyName("designers")]
    public List<string> Designers { get; set; } = [];

    /// <summary>Theme tags from OPDB.</summary>
    [JsonPropertyName("themes")]
    public List<string> Themes { get; set; } = [];

    /// <summary>Editions (Pro / Premium / LE / Vault, etc.) shipped under this OPDB ID.</summary>
    [JsonPropertyName("editions")]
    public List<MachineEdition> Editions { get; set; } = [];

    /// <summary>
    /// Edition-qualified OPDB label for this base when it shares a franchise
    /// (GroupId) with sibling bases — e.g. "Pro", "Premium/LE". Derived from the
    /// parenthetical of OPDB's edition-qualified name. Null for singleton machines.
    /// NOT the Title — Title stays the clean franchise name per ADR-0029 D1.
    /// </summary>
    [JsonPropertyName("editionLabel")]
    public string? EditionLabel { get; set; }

    /// <summary>
    /// Normalized edition tokens this base answers to — e.g. ["pro"] for the Pro
    /// base, ["premium","le","70th"] for the Premium/LE base (folded from its
    /// alias editions). The reliable per-base discriminator the linker matches a
    /// document's edition token against (NOT Title). Empty for singletons.
    /// </summary>
    [JsonPropertyName("editionTokens")]
    public List<string> EditionTokens { get; set; } = [];

    /// <summary>Manufacturer-specific identifiers — e.g., {"stern": "stranger-things", "jjp": "..."}.</summary>
    [JsonPropertyName("manufacturerSlugs")]
    public Dictionary<string, string> ManufacturerSlugs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>OPDB record source URL for verification.</summary>
    [JsonPropertyName("opdbSourceUrl")]
    public string? OpdbSourceUrl { get; set; }

    /// <summary>When the machine record was first seen by our ingestion.</summary>
    [JsonPropertyName("firstSeenAt")]
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Last time the OPDB sync confirmed this record exists.</summary>
    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Cosmos system-managed _etag, populated on read for optimistic concurrency.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// One edition (model variant) of a <see cref="Machine"/>. A given
/// Stranger Things release ships as Pro, Premium, and LE — each is a
/// MachineEdition under the same parent Machine.
/// </summary>
public sealed class MachineEdition
{
    /// <summary>Edition name (e.g., "Pro", "Premium", "Limited Edition").</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Manufacturer's published MSRP at launch — string-typed because the source format varies.</summary>
    [JsonPropertyName("msrp")]
    public string? Msrp { get; set; }

    /// <summary>Limited-quantity number for LE / collector editions. Null for unlimited Pro / Premium.</summary>
    [JsonPropertyName("limitedQuantity")]
    public int? LimitedQuantity { get; set; }

    /// <summary>Short-form availability label as published by the manufacturer.</summary>
    [JsonPropertyName("availability")]
    public string? Availability { get; set; }

    /// <summary>Free-text description of what makes this edition different.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Edition-unique features called out by the manufacturer.</summary>
    [JsonPropertyName("uniqueFeatures")]
    public List<string> UniqueFeatures { get; set; } = [];

    /// <summary>
    /// OPDB's canonical 3-segment ID for this edition (e.g.,
    /// <c>GRBN-MQR4P-A97X1</c>). Populated when the edition originated from
    /// an OPDB alias record; null for editions sourced from manufacturer
    /// scrapers or hand-authored data. Carrying the alias ID forward
    /// preserves the provenance chain — the Phase 2 RAG layer cites this
    /// ID via <see cref="OpdbSourceUrl"/> when answering edition-specific
    /// questions ("what's the difference between Stranger Things Premium
    /// LE and Pro?").
    /// </summary>
    [JsonPropertyName("opdbAliasId")]
    public string? OpdbAliasId { get; set; }

    /// <summary>
    /// OPDB record URL for this edition (e.g.,
    /// <c>https://opdb.org/search?q=GRBN-MQR4P-A97X1</c> — opdb.org has
    /// no /machines/{opdb_id} route; search-by-id is the durable deep
    /// link). Populated alongside <see cref="OpdbAliasId"/>. The base
    /// machine's <c>OpdbSourceUrl</c> on <see cref="Machine"/> covers
    /// the parent record; this field covers the alias.
    /// </summary>
    [JsonPropertyName("opdbSourceUrl")]
    public string? OpdbSourceUrl { get; set; }
}
