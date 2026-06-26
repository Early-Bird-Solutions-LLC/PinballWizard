using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.Kineticist;

// Models for the Kineticist public API (v1), ADR-0043 Tier A. The games
// catalog is OPDB-keyed: a game detail carries an `editions[]` array whose
// `opdb_id` values join directly to our OPDB-keyed machine catalog. This
// replaces fuzzy title-matching for tutorial→machine linking.
//
// Public result types (returned from IKineticistApiClient) are deliberately
// thin — only the fields the linker needs. The internal records below mirror
// the wire JSON (snake_case via JsonPropertyName).

/// <summary>
/// A resolved Kineticist game: its canonical slug/name and the OPDB ids of
/// every edition (trim) the game has. The edition opdb ids are full
/// <c>{group}-{machine}</c> identifiers (e.g. <c>Gr3EW-MD3Nj</c>) that match
/// the keys in our machine catalog.
/// </summary>
public sealed record KineticistGameMatch(
    string Slug,
    string Name,
    IReadOnlyList<string> EditionOpdbIds);

/// <summary>A lightweight game reference from a search result (name + slug).</summary>
public sealed record KineticistGameRef(string Name, string Slug);

// ── Wire DTOs (internal) ─────────────────────────────────────────────────

internal sealed record KineticistGameDetailResponse(
    [property: JsonPropertyName("data")] KineticistGameData? Data);

internal sealed record KineticistGameData(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("opdb_id")] string? OpdbId,
    [property: JsonPropertyName("editions")] IReadOnlyList<KineticistEditionDto>? Editions);

internal sealed record KineticistEditionDto(
    [property: JsonPropertyName("opdb_id")] string? OpdbId,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record KineticistGameListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<KineticistGameSummaryDto>? Data);

internal sealed record KineticistGameSummaryDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("slug")] string? Slug);
