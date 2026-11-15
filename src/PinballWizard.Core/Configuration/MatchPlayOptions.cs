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
}
