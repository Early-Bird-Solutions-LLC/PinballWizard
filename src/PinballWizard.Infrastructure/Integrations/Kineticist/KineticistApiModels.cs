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

/// <summary>
/// ADR-0043 Tier B guide deep-links from a Kineticist game detail. The keyed
/// API returns URLs only — never guide body content (still no Tier C1).
/// Verified 2026-11-07 on <c>GET /games/godzilla-2021</c>.
/// </summary>
public sealed record KineticistGuideLinks(
    string Slug,
    string Name,
    string? OpdbId,
    string? FullTutorialLink,
    string? GameRulesLink,
    string? PinballPrimerUrl)
{
    /// <summary>
    /// True when at least one deep-link URL is present. Callers treat a
    /// missing-links result as unresolved (fail closed — no scrape fallback).
    /// </summary>
    public bool HasAnyLink =>
        !string.IsNullOrWhiteSpace(FullTutorialLink)
        || !string.IsNullOrWhiteSpace(GameRulesLink)
        || !string.IsNullOrWhiteSpace(PinballPrimerUrl);

    /// <summary>
    /// Primary attribution URL: Kineticist's own tutorial when present,
    /// otherwise the external rules link, otherwise the primer.
    /// </summary>
    public string? PrimaryAttributionUrl =>
        FirstNonEmpty(FullTutorialLink, GameRulesLink, PinballPrimerUrl);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }
}

// ── Wire DTOs (internal) ─────────────────────────────────────────────────

internal sealed record KineticistGameDetailResponse(
    [property: JsonPropertyName("data")] KineticistGameData? Data);

internal sealed record KineticistGameData(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("opdb_id")] string? OpdbId,
    [property: JsonPropertyName("editions")] IReadOnlyList<KineticistEditionDto>? Editions,
    // Tier B deep-link fields (flat on data — verified 2026-11-07).
    [property: JsonPropertyName("full_tutorial_link")] string? FullTutorialLink = null,
    [property: JsonPropertyName("game_rules_link")] string? GameRulesLink = null,
    [property: JsonPropertyName("pinball_primer_url")] string? PinballPrimerUrl = null);

internal sealed record KineticistEditionDto(
    [property: JsonPropertyName("opdb_id")] string? OpdbId,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record KineticistGameListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<KineticistGameSummaryDto>? Data);

internal sealed record KineticistGameSummaryDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("slug")] string? Slug);
