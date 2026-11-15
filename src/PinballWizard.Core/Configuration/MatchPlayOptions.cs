using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the Match Play Events live-tournament integration (ADR-0061).
// Bearer token is the same Match Play API credential family as Opdb:ApiToken
// (Key Vault Opdb-ApiToken / OPDB__APITOKEN in local dev). Prefer an explicit
// MatchPlay:ApiToken when set; DI falls back to Opdb:ApiToken so a single
// secret continues to cover OPDB entry GETs and tournament tools.
//
// DI registration is gated on token presence — absent config means
// IMatchPlayTournamentProvider is not registered and the Match Play Wizard
// tools degrade gracefully (return null).
public sealed class MatchPlayOptions
{
    public const string SectionName = "MatchPlay";

    // Full key for the bearer token — used by DI-gating logic.
    public const string ApiTokenKey = $"{SectionName}:ApiToken";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://api.matchplay.events/";

    // Bearer token for Authorization: Bearer … on all Match Play API calls.
    // Empty at bind time is allowed only when the gate used Opdb:ApiToken —
    // AddMatchPlayIntegration copies that fallback into this property.
    public string ApiToken { get; set; } = string.Empty;

    // Per-request HTTP timeout. Tournament search + standings are small JSON.
    [Range(5, 600)]
    public int HttpTimeoutSeconds { get; set; } = 30;

    // Minimum interval between standings fetches for the same in-progress
    // tournament. Match Play handbook: do not request standings more often
    // than once every 15 seconds while a tournament is in progress.
    [Range(15, 600)]
    public int StandingsMinIntervalSeconds { get; set; } = 15;

    // Match Play Ratings CDN export (ADR-0063). Public object — no bearer.
    // Handbook: fetch ratings from the CDN, not the API bulk surface.
    [Url]
    public string RatingsExportUrl { get; set; } =
        "https://mp-ratings.sfo3.cdn.digitaloceanspaces.com/latest-ratings.csv";

    // How long a process caches the parsed ratings index after a CDN fetch.
    // Export refreshes daily; 12h balances freshness vs. 8MB download cost.
    [Range(1, 168)]
    public int RatingsCacheHours { get; set; } = 12;

    // HTTP timeout for the ratings CSV download (larger than JSON tournament calls).
    [Range(30, 600)]
    public int RatingsHttpTimeoutSeconds { get; set; } = 120;
}
