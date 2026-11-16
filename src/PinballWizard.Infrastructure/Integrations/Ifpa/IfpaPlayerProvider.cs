using PinballWizard.Application.Players;

namespace PinballWizard.Infrastructure.Integrations.Ifpa;

// IIfpaPlayerProvider backed by the IFPA API (ADR-0067).
internal sealed class IfpaPlayerProvider : IIfpaPlayerProvider
{
    private readonly IIfpaClient _client;

    public IfpaPlayerProvider(IIfpaClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<IReadOnlyList<IfpaPlayerSearchHit>> SearchPlayersAsync(
        string name,
        string? country = null,
        string? state = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var hits = await _client
            .SearchPlayersAsync(name.Trim(), country, state, cancellationToken)
            .ConfigureAwait(false);

        return hits
            .Where(h => h.PlayerId is > 0)
            .Select(h =>
            {
                var id = (int)h.PlayerId!.Value;
                return new IfpaPlayerSearchHit(
                    id,
                    h.FirstName ?? string.Empty,
                    h.LastName ?? string.Empty,
                    NullIfWhiteSpace(h.City),
                    NullIfWhiteSpace(h.StateProv),
                    NullIfWhiteSpace(h.CountryName),
                    NullIfWhiteSpace(h.CountryCode),
                    ToNullableInt(h.WpprRank),
                    h.RatingValue,
                    PublicPlayerUrl(id));
            })
            .ToList();
    }

    public async Task<IfpaPlayerProfile?> GetPlayerAsync(
        int playerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(playerId);

        var wire = await _client.GetPlayerAsync(playerId, cancellationToken).ConfigureAwait(false);
        if (wire is null || wire.PlayerId is not > 0)
        {
            return null;
        }

        var id = (int)wire.PlayerId.Value;
        var systems = wire.PlayerStats?.System;
        // Live API keys are `open` / `womens`; Application + Wizard.md still
        // speak MAIN / WOMEN. Prefer live keys, fall back to historical names.
        return new IfpaPlayerProfile(
            id,
            wire.FirstName ?? string.Empty,
            wire.LastName ?? string.Empty,
            NullIfWhiteSpace(wire.Initials),
            NullIfWhiteSpace(wire.City),
            NullIfWhiteSpace(wire.StateProv),
            NullIfWhiteSpace(wire.CountryName),
            NullIfWhiteSpace(wire.CountryCode),
            wire.IfpaRegistered,
            PublicPlayerUrl(id),
            MapStanding("MAIN", systems?.Open ?? systems?.Main),
            MapStanding("WOMEN", systems?.Womens ?? systems?.Women));
    }

    internal static string PublicPlayerUrl(int playerId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"https://www.ifpapinball.com/player.php?p={playerId}");

    private static IfpaWpprStanding? MapStanding(string system, IfpaRankingStandingWireDto? wire)
    {
        if (wire is null)
        {
            return null;
        }

        // Empty system object → treat as absent (no rank/points at all).
        if (wire.CurrentRank is null
            && wire.CurrentPoints is null
            && wire.ActivePoints is null
            && wire.RatingsRank is null
            && wire.RatingsValue is null)
        {
            return null;
        }

        return new IfpaWpprStanding(
            system,
            ToNullableInt(wire.CurrentRank),
            ToNullableInt(wire.LastMonthRank),
            wire.CurrentPoints,
            wire.ActivePoints,
            ToNullableInt(wire.RatingsRank),
            wire.RatingsValue,
            ToNullableInt(wire.TotalEventsAllTime),
            ToNullableInt(wire.TotalActiveEvents));
    }

    private static int? ToNullableInt(double? value) =>
        value is null ? null : (int)value.Value;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
