using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

/// <summary>
/// Wire DTO for an OPDB machine record. Mirrors the JSON shape OPDB's
/// <c>/api/machines</c> endpoint returns; intentionally tolerant of
/// missing fields (OPDB's older records have sparse metadata).
/// </summary>
/// <remarks>
/// Source: OPDB API (<a href="https://opdb.org/api">opdb.org/api</a>).
/// Field names are documented as snake_case; the property mappings use
/// <see cref="JsonPropertyNameAttribute"/> rather than relying on a
/// global naming policy so the wire format is explicit at the type.
/// </remarks>
public sealed class OpdbMachineDto
{
    /// <summary>OPDB canonical identifier (e.g., <c>GRBN-MQR4P</c>).</summary>
    [JsonPropertyName("opdb_id")]
    public string? OpdbId { get; init; }

    /// <summary>Whether this record represents a machine (vs a related entity).</summary>
    [JsonPropertyName("is_machine")]
    public bool IsMachine { get; init; }

    /// <summary>
    /// Whether this record represents an alias (variant / LE edition) of a
    /// base machine. Aliases share the first two OPDB ID segments with their
    /// base machine and add a third (e.g., base <c>GRoz4-MrRPw</c>, alias
    /// <c>GRoz4-MrRPw-A97X1</c>). OPDB sets <c>is_alias=true</c> on aliases
    /// and omits <c>is_machine</c> entirely, so an alias deserializes with
    /// <see cref="IsMachine"/>=<c>false</c> and <see cref="IsAlias"/>=
    /// <c>true</c>. The sync service folds aliases into the base machine's
    /// <c>Editions</c> list.
    /// </summary>
    [JsonPropertyName("is_alias")]
    public bool IsAlias { get; init; }

    /// <summary>
    /// OPDB's <c>physical_machine</c> flag (1 = hardware record,
    /// 0 = an edition-grouping record whose 3-segment aliases carry the
    /// real variants). Captured for provenance and diagnostics ONLY.
    /// Per <see href="../../../docs/adr/0029-version-aware-answering.md">ADR-0029</see>
    /// this is explicitly NOT a canonical-selection signal: it is a 7.3%
    /// minority pattern (most multi-base groups are all <c>pm:1</c>), and
    /// a prior model that picked a "canonical" row by it was rejected.
    /// Every 2-segment <c>is_machine</c> record is a distinct machine
    /// regardless of this value. Nullable: absent on alias records.
    /// </summary>
    [JsonPropertyName("physical_machine")]
    public int? PhysicalMachine { get; init; }

    /// <summary>Full name including edition suffix (e.g., "Stranger Things (Pro)").</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Short name without edition suffix (e.g., "Stranger Things").</summary>
    [JsonPropertyName("common_name")]
    public string? CommonName { get; init; }

    /// <summary>Manufacturer block.</summary>
    [JsonPropertyName("manufacturer")]
    public OpdbManufacturerDto? Manufacturer { get; init; }

    /// <summary>Manufacture date (release date) — OPDB returns YYYY-MM-DD or null.</summary>
    [JsonPropertyName("manufacture_date")]
    public string? ManufactureDate { get; init; }

    /// <summary>OPDB's "type" field — usually <c>ss</c> (solid state), <c>em</c> (electromechanical), etc.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Designer credits (zero or more).</summary>
    [JsonPropertyName("designers")]
    public List<OpdbPersonDto> Designers { get; init; } = [];

    /// <summary>Theme keywords / tags.</summary>
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; init; } = [];

    /// <summary>OPDB edition "features" (e.g. ["Pro edition"]). Secondary edition
    /// signal — used only as the EditionLabel fallback when Name has no parenthetical.</summary>
    [JsonPropertyName("features")]
    public List<string> Features { get; init; } = [];

    /// <summary>Last update timestamp.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// IPDB numeric machine ID — present when OPDB has matched this machine to
    /// the Internet Pinball Machine Database. Used to compute
    /// <c>ipdbReferenceUrl</c> on the <see cref="PinballWizard.Core.Domain.Machine"/>
    /// Cosmos record. Null for machines with no IPDB entry (rare; mostly
    /// non-English-market or unlicensed titles).
    /// </summary>
    [JsonPropertyName("ipdb_id")]
    public int? IpdbId { get; init; }
}

/// <summary>
/// Wire DTO for an OPDB <c>is_machine_group</c> record — the top tier of
/// OPDB's three-tier structure (group → base machine → alias). Returned
/// by <c>GET /api/machines/{groupSegment}</c> where the segment is the
/// leading part of an OPDB ID before the first hyphen (e.g. <c>GweeP</c>
/// for the Godzilla group). Carries the clean franchise title
/// ("Godzilla") which is absent from individual records' empty
/// <c>common_name</c> on modern Stern machines.
/// </summary>
/// <remarks>
/// This record is NOT present in the bulk <c>/api/export</c> feed — it
/// is only reachable via the per-segment endpoint. Per
/// <see href="../../../docs/adr/0029-version-aware-answering.md">ADR-0029</see>
/// the group is a <em>relational</em> tier (used to resolve a clean
/// title and discover sibling machines), never a merge target — every
/// base machine remains a distinct <c>Machine</c>.
/// </remarks>
public sealed class OpdbMachineGroupDto
{
    /// <summary>The group segment (e.g., <c>GweeP</c>).</summary>
    [JsonPropertyName("opdb_id")]
    public string? OpdbId { get; init; }

    /// <summary>True when this is a machine-group record (vs a machine/alias).</summary>
    [JsonPropertyName("is_machine_group")]
    public bool IsMachineGroup { get; init; }

    /// <summary>Clean franchise title without edition suffix (e.g., "Godzilla").</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Short / abbreviated group name if OPDB has one.</summary>
    [JsonPropertyName("shortname")]
    public string? ShortName { get; init; }
}

/// <summary>Manufacturer block within an OPDB machine record.</summary>
public sealed class OpdbManufacturerDto
{
    /// <summary>OPDB-internal manufacturer id.</summary>
    [JsonPropertyName("manufacturer_id")]
    public int ManufacturerId { get; init; }

    /// <summary>Display name (e.g., "Stern Pinball, Inc.").</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Short / abbreviated name if OPDB has one.</summary>
    [JsonPropertyName("shortname")]
    public string? ShortName { get; init; }
}

/// <summary>Person credit (designer / artist / programmer / etc.).</summary>
public sealed class OpdbPersonDto
{
    /// <summary>OPDB-internal person id.</summary>
    [JsonPropertyName("person_id")]
    public int PersonId { get; init; }

    /// <summary>Person's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
