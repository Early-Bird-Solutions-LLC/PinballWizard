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

    /// <summary>Last update timestamp.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
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
