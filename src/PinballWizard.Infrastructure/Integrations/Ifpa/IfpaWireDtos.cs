using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.Ifpa;

// Wire DTOs for IFPA OpenAPI 2.1 player endpoints (ADR-0067).
// Property names match the API's snake_case JSON.

public sealed class IfpaPlayerSearchResponseDto
{
    [JsonPropertyName("count")]
    public double? Count { get; set; }

    [JsonPropertyName("total_results")]
    public double? TotalResults { get; set; }

    // OpenAPI incorrectly types results as a single object; the live API
    // returns an array. Deserialize as JsonElement and normalize in the client.
    [JsonPropertyName("results")]
    public JsonElement Results { get; set; }
}

public sealed class IfpaPlayerSearchHitWireDto
{
    [JsonPropertyName("player_id")]
    public double? PlayerId { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("country_name")]
    public string? CountryName { get; set; }

    [JsonPropertyName("country_code")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("stateprov")]
    public string? StateProv { get; set; }

    [JsonPropertyName("wppr_rank")]
    public double? WpprRank { get; set; }

    [JsonPropertyName("rating_value")]
    public double? RatingValue { get; set; }
}

public sealed class IfpaPlayerEnvelopeDto
{
    // OpenAPI types this as one object. The live profile endpoint returns
    // a one-element array, the same mismatch search results already normalize.
    [JsonPropertyName("player")]
    public JsonElement Player { get; set; }
}

public sealed class IfpaPlayerWireDto
{
    [JsonPropertyName("player_id")]
    public double? PlayerId { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("initials")]
    public string? Initials { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("stateprov")]
    public string? StateProv { get; set; }

    [JsonPropertyName("country_name")]
    public string? CountryName { get; set; }

    [JsonPropertyName("country_code")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("ifpa_registered")]
    public bool? IfpaRegistered { get; set; }

    [JsonPropertyName("player_stats")]
    public IfpaPlayerStatsWireDto? PlayerStats { get; set; }
}

public sealed class IfpaPlayerStatsWireDto
{
    [JsonPropertyName("system")]
    public IfpaRankingSystemsWireDto? System { get; set; }
}

public sealed class IfpaRankingSystemsWireDto
{
    [JsonPropertyName("MAIN")]
    public IfpaRankingStandingWireDto? Main { get; set; }

    [JsonPropertyName("WOMEN")]
    public IfpaRankingStandingWireDto? Women { get; set; }
}

public sealed class IfpaRankingStandingWireDto
{
    [JsonPropertyName("current_rank")]
    public double? CurrentRank { get; set; }

    [JsonPropertyName("last_month_rank")]
    public double? LastMonthRank { get; set; }

    [JsonPropertyName("current_points")]
    public double? CurrentPoints { get; set; }

    [JsonPropertyName("active_points")]
    public double? ActivePoints { get; set; }

    [JsonPropertyName("ratings_rank")]
    public double? RatingsRank { get; set; }

    [JsonPropertyName("ratings_value")]
    public double? RatingsValue { get; set; }

    [JsonPropertyName("total_events_all_time")]
    public double? TotalEventsAllTime { get; set; }

    [JsonPropertyName("total_active_events")]
    public double? TotalActiveEvents { get; set; }
}
