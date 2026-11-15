using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

/// <summary>
/// Wire DTO for an OPDB machine or alias record from the Match Play
/// OPDB V1 bulk export. Field names are camelCase (Match Play CDN
/// format); older snake_case opdb.org payloads are no longer served.
/// </summary>
/// <remarks>
/// Source: Match Play OPDB V1 export
/// (<a href="https://docs.matchplay.events/data-exports">docs.matchplay.events/data-exports</a>).
/// Property mappings use <see cref="JsonPropertyNameAttribute"/> so the
/// wire format is explicit at the type.
/// </remarks>
public sealed class OpdbMachineDto
{
    /// <summary>OPDB canonical identifier (e.g., <c>GRBN-MQR4P</c>).</summary>
    [JsonPropertyName("opdbId")]
    public string? OpdbId { get; init; }

    /// <summary>Whether this record represents a machine (vs a related entity).</summary>
    [JsonPropertyName("isMachine")]
    public bool IsMachine { get; init; }

    /// <summary>
    /// Whether this record represents an alias (variant / LE edition) of a
    /// base machine. Aliases share the first two OPDB ID segments with their
    /// base machine and add a third (e.g., base <c>GRoz4-MrRPw</c>, alias
    /// <c>GRoz4-MrRPw-A97X1</c>). OPDB sets <c>isAlias=true</c> on aliases
    /// and omits <c>isMachine</c> entirely, so an alias deserializes with
    /// <see cref="IsMachine"/>=<c>false</c> and <see cref="IsAlias"/>=
    /// <c>true</c>. The sync service folds aliases into the base machine's
    /// <c>Editions</c> list.
    /// </summary>
    [JsonPropertyName("isAlias")]
    public bool IsAlias { get; init; }

    /// <summary>
    /// OPDB's <c>physicalMachine</c> flag (1 = hardware record,
    /// 0 = an edition-grouping record whose 3-segment aliases carry the
    /// real variants). Captured for provenance and diagnostics ONLY.
    /// Per <see href="../../../docs/adr/0029-version-aware-answering.md">ADR-0029</see>
    /// this is explicitly NOT a canonical-selection signal: it is a 7.3%
    /// minority pattern (most multi-base groups are all <c>pm:1</c>), and
    /// a prior model that picked a "canonical" row by it was rejected.
    /// Every 2-segment <c>isMachine</c> record is a distinct machine
    /// regardless of this value. Nullable: absent on alias records.
    /// </summary>
    [JsonPropertyName("physicalMachine")]
    public int? PhysicalMachine { get; init; }

    /// <summary>Full name including edition suffix (e.g., "Stranger Things (Pro)").</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Short name without edition suffix (e.g., "Stranger Things").</summary>
    [JsonPropertyName("commonName")]
    public string? CommonName { get; init; }

    /// <summary>Manufacturer block.</summary>
    [JsonPropertyName("manufacturer")]
    public OpdbManufacturerDto? Manufacturer { get; init; }

    /// <summary>Manufacture date (release date) — OPDB returns YYYY-MM-DD or null.</summary>
    [JsonPropertyName("manufactureDate")]
    public string? ManufactureDate { get; init; }

    /// <summary>OPDB's "type" field — usually <c>ss</c> (solid state), <c>em</c> (electromechanical), etc.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Designer credits (zero or more). Absent from the V1 CDN export today.</summary>
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
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// IPDB numeric machine ID — present when OPDB has matched this machine to
    /// the Internet Pinball Machine Database. Used to compute
    /// <c>ipdbReferenceUrl</c> on the <see cref="PinballWizard.Core.Domain.Machine"/>
    /// Cosmos record. Null for machines with no IPDB entry (rare; mostly
    /// non-English-market or unlicensed titles).
    /// </summary>
    [JsonPropertyName("ipdbId")]
    public int? IpdbId { get; init; }
}

/// <summary>
/// Wire DTO for an OPDB <c>isMachineGroup</c> record — the top tier of
/// OPDB's three-tier structure (group → base machine → alias). Present in
/// the Match Play V1 export's <c>machineGroups</c> array (and optionally
/// via Match Play <c>GET /api/opdb/entry/{segment}</c>). Carries the clean
/// franchise title ("Godzilla") which is absent from individual records'
/// empty <c>commonName</c> on modern Stern machines.
/// </summary>
/// <remarks>
/// Per <see href="../../../docs/adr/0029-version-aware-answering.md">ADR-0029</see>
/// the group is a <em>relational</em> tier (used to resolve a clean
/// title and discover sibling machines), never a merge target — every
/// base machine remains a distinct <c>Machine</c>.
/// </remarks>
public sealed class OpdbMachineGroupDto
{
    /// <summary>The group segment (e.g., <c>GweeP</c>).</summary>
    [JsonPropertyName("opdbId")]
    public string? OpdbId { get; init; }

    /// <summary>True when this is a machine-group record (vs a machine/alias).</summary>
    [JsonPropertyName("isMachineGroup")]
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
    [JsonPropertyName("manufacturerId")]
    public int ManufacturerId { get; init; }

    /// <summary>
    /// Short display name (e.g., "Stern"). Match Play V1 also exposes
    /// <see cref="FullName"/> for the legal / long form.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Legal / long manufacturer name (e.g., "Stern Pinball, Inc.").</summary>
    [JsonPropertyName("fullName")]
    public string? FullName { get; init; }

    /// <summary>Short / abbreviated name if OPDB has one (legacy field; often absent in V1).</summary>
    [JsonPropertyName("shortname")]
    public string? ShortName { get; init; }
}

/// <summary>Person credit (designer / artist / programmer / etc.).</summary>
public sealed class OpdbPersonDto
{
    /// <summary>OPDB-internal person id.</summary>
    [JsonPropertyName("personId")]
    public int PersonId { get; init; }

    /// <summary>Person's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
