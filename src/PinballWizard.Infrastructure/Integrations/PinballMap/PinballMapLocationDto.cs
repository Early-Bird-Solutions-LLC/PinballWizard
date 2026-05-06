using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.PinballMap;

/// <summary>
/// Wire DTO for a single Pinball Map location. Mirrors the JSON shape
/// returned by <c>/api/v1/region/{region}/locations.json</c> as
/// observed against the live API on 2026-05-04. Tolerant of missing
/// fields — older / sparse records frequently omit <c>operator_id</c>,
/// <c>zone_id</c>, <c>place_id</c>, <c>description</c>, etc.
/// </summary>
/// <remarks>
/// Field names on the wire are snake_case. The property mappings use
/// <see cref="JsonPropertyNameAttribute"/> rather than relying on a
/// global naming policy so the wire format is explicit at the type.
/// <para>
/// The DTO captures only the fields the integration currently needs
/// (identity, geocoded position, on-site machine cross-references with
/// OPDB linkage). Adding new fields is non-breaking; renaming a wire
/// field at the source would be the only failure mode.
/// </para>
/// </remarks>
public sealed class PinballMapLocationDto
{
    /// <summary>Pinball Map internal location id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Display name of the location (e.g., "2Bears Tavern Uptown").</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Street address (single line).</summary>
    [JsonPropertyName("street")]
    public string? Street { get; init; }

    /// <summary>City.</summary>
    [JsonPropertyName("city")]
    public string? City { get; init; }

    /// <summary>State / province (free-form, e.g., "IL").</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>Postal code.</summary>
    [JsonPropertyName("zip")]
    public string? Zip { get; init; }

    /// <summary>ISO country code (e.g., "US"). May be null on legacy records.</summary>
    [JsonPropertyName("country")]
    public string? Country { get; init; }

    /// <summary>Phone number (free-form).</summary>
    [JsonPropertyName("phone")]
    public string? Phone { get; init; }

    /// <summary>Latitude as a string (the API serializes lat/lon as strings).</summary>
    [JsonPropertyName("lat")]
    public string? Latitude { get; init; }

    /// <summary>Longitude as a string.</summary>
    [JsonPropertyName("lon")]
    public string? Longitude { get; init; }

    /// <summary>Optional operator-supplied URL.</summary>
    [JsonPropertyName("website")]
    public string? Website { get; init; }

    /// <summary>Free-form description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Region id this location belongs to.</summary>
    [JsonPropertyName("region_id")]
    public int? RegionId { get; init; }

    /// <summary>Number of machines currently reported at the location.</summary>
    [JsonPropertyName("num_machines")]
    public int? NumMachines { get; init; }

    /// <summary>Last time the location record itself was updated (server time).</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Cross-references between the location and the machines on-site.
    /// Each entry carries the link-table id plus an embedded
    /// <see cref="PinballMapMachineDto"/> with the canonical OPDB id —
    /// this is the bridge from a Pinball Map location to our machine
    /// catalog.
    /// </summary>
    [JsonPropertyName("location_machine_xrefs")]
    public List<PinballMapLocationMachineXrefDto> LocationMachineXrefs { get; init; } = [];
}

/// <summary>Wrapper for the <c>locations</c> array returned by the locations endpoint.</summary>
public sealed class PinballMapLocationsResponse
{
    /// <summary>The locations payload.</summary>
    [JsonPropertyName("locations")]
    public List<PinballMapLocationDto> Locations { get; init; } = [];
}

/// <summary>Cross-reference linking a location to a machine on-site.</summary>
public sealed class PinballMapLocationMachineXrefDto
{
    /// <summary>Cross-reference id (rarely used; kept for completeness).</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Location id this xref belongs to.</summary>
    [JsonPropertyName("location_id")]
    public int LocationId { get; init; }

    /// <summary>Pinball Map machine id (numeric).</summary>
    [JsonPropertyName("machine_id")]
    public int MachineId { get; init; }

    /// <summary>Embedded machine record — the OPDB-linkable detail.</summary>
    [JsonPropertyName("machine")]
    public PinballMapMachineDto? Machine { get; init; }
}

/// <summary>
/// Embedded machine record on a location xref. Mirrors the fields the
/// public Pinball Map API exposes; <see cref="OpdbId"/> is the bridge
/// to our canonical machine catalog.
/// </summary>
public sealed class PinballMapMachineDto
{
    /// <summary>Pinball Map internal machine id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Display name including edition (e.g., "Star Wars (Pro)").</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Manufacturer name (free-form, e.g., "Stern").</summary>
    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; init; }

    /// <summary>Year of manufacture.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; init; }

    /// <summary>OPDB canonical id (e.g., "G5vLR-MwNwy"). The bridge to <c>OpdbMachineDto.OpdbId</c>.</summary>
    [JsonPropertyName("opdb_id")]
    public string? OpdbId { get; init; }

    /// <summary>IPDB numeric id (when present — older machines often have IPDB but no OPDB).</summary>
    [JsonPropertyName("ipdb_id")]
    public int? IpdbId { get; init; }

    /// <summary>External link to IPDB.</summary>
    [JsonPropertyName("ipdb_link")]
    public string? IpdbLink { get; init; }
}
