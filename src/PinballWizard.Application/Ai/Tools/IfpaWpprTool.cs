using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Players;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tools for live IFPA WPPR player data (ADR-0067).
// Wizard-only — ToolTraceCitationExtractor observes Wizard FunctionResultContent.
//
// IIfpaPlayerProvider is optional (nullable DI). When absent — because
// Ifpa:ApiKey is not configured — tools return null immediately.
//
// CancellationToken defaults are load-bearing for AIFunctionFactory schemas
// (same rationale as MarketValueTool / MatchPlayTournamentTool).
//
// Fail closed on unexpected faults — return null so the Wizard can degrade
// honestly rather than aborting the turn. Broad catch is intentional
// (Invariant #17 / ADR-0067): mirrors MarketValueTool / MatchPlayTournamentTool.
// Narrowing would let an unanticipated exception type crash the Wizard turn.
// CodeQL cs/catch-of-all-exceptions → won't-fix (precedent PR #484).
public sealed class IfpaWpprTool
{
    internal const string SearchToolTag = "searchIfpaPlayers";
    internal const string ProfileToolTag = "getIfpaPlayer";

    private readonly IIfpaPlayerProvider? _provider;
    private readonly ILogger<IfpaWpprTool> _logger;

    public IfpaWpprTool(
        ILogger<IfpaWpprTool> logger,
        IIfpaPlayerProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _provider = provider;
    }

    [Description("Search IFPA players by name (optional country / state filters). Returns up to 10 candidates with playerId, location, and WPPR rank when ranked. Use before getIfpaPlayer when the user named a player rather than a numeric IFPA id. Returns null when IFPA is not configured or the search yields nothing. Never invent players.")]
    public async Task<IfpaPlayerSearchResultDto?> SearchIfpaPlayersAsync(
        [Description("Player name fragment (partial match, case insensitive).")] string name,
        [Description("Optional country name or 2-letter code (e.g. US, United States).")] string? country = null,
        [Description("Optional 2-letter state/province code (e.g. IL, WA).")] string? state = null,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(SearchToolTag) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmedName = name.Trim();
        var trimmedCountry = string.IsNullOrWhiteSpace(country) ? null : country.Trim();
        var trimmedState = string.IsNullOrWhiteSpace(state) ? null : state.Trim();

        return await RunAsync(
            SearchToolTag,
            async () =>
            {
                var hits = await _provider!
                    .SearchPlayersAsync(trimmedName, trimmedCountry, trimmedState, cancellationToken)
                    .ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return null;
                }

                var mapped = hits.Take(10)
                    .Select(h => new IfpaPlayerSearchHitDto(
                        h.PlayerId,
                        h.FirstName,
                        h.LastName,
                        h.City,
                        h.StateProv,
                        h.CountryName,
                        h.CountryCode,
                        h.WpprRank,
                        h.RatingValue,
                        h.PublicUrl,
                        Attribution(h.PublicUrl)))
                    .ToList();
                var first = mapped[0];
                return new IfpaPlayerSearchResultDto(
                    trimmedName,
                    trimmedCountry,
                    trimmedState,
                    first.PublicUrl,
                    Attribution(first.PublicUrl),
                    mapped);
            },
            ex => FailNull<IfpaPlayerSearchResultDto>(ex, SearchToolTag, $"name={trimmedName}"),
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Get an IFPA player profile and WPPR standing by numeric playerId. Returns MAIN (and WOMEN when present) current rank, points, and ratings, plus a public IFPA player URL for attribution. Prefer searching first when the user gave a name. Returns null when IFPA is not configured, the id is unknown, or the API fails. Never invent ranks or points.")]
    public async Task<IfpaPlayerProfileDto?> GetIfpaPlayerAsync(
        [Description("IFPA player id (integer from searchIfpaPlayers or a known IFPA profile).")] int playerId,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(ProfileToolTag) || !ValidId(playerId, ProfileToolTag))
        {
            return null;
        }

        return await RunAsync(
            ProfileToolTag,
            async () =>
            {
                var result = await _provider!
                    .GetPlayerAsync(playerId, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    return null;
                }

                return new IfpaPlayerProfileDto(
                    result.PlayerId,
                    result.FirstName,
                    result.LastName,
                    result.Initials,
                    result.City,
                    result.StateProv,
                    result.CountryName,
                    result.CountryCode,
                    result.IfpaRegistered,
                    result.PublicUrl,
                    Attribution(result.PublicUrl),
                    MapStanding(result.Main),
                    MapStanding(result.Women));
            },
            ex => FailNull<IfpaPlayerProfileDto>(ex, ProfileToolTag, $"playerId={playerId}"),
            cancellationToken).ConfigureAwait(false);
    }

    private bool EnsureProvider(string toolTag)
    {
        if (_provider is not null)
        {
            return true;
        }

        _logger.LogDebug(
            "IfpaWpprTool: provider not registered — returning null from {Tool}",
            toolTag);
        return false;
    }

    private bool ValidId(int playerId, string toolTag)
    {
        if (playerId > 0)
        {
            return true;
        }

        _logger.LogDebug(
            "IfpaWpprTool: invalid playerId={PlayerId} for {Tool}",
            playerId,
            toolTag);
        return false;
    }

    private async Task<T?> RunAsync<T>(
        string toolTag,
        Func<Task<T?>> action,
        Func<Exception, T?> onFault,
        CancellationToken _)
        where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentional boundary catch (Invariant #17): log/meter via onFault,
        // return null. Do not narrow — unanticipated types must not abort the turn.
        catch (Exception ex)
        {
            return onFault(ex);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", toolTag));
        }
    }

    private T? FailNull<T>(Exception ex, string toolTag, string context)
        where T : class
    {
        PinballWizardTelemetry.AiToolErrors.Add(
            1,
            new KeyValuePair<string, object?>("tool", toolTag));
        _logger.LogWarning(ex, "IfpaWpprTool: {Tool} failed {Context}", toolTag, context);
        return null;
    }

    private static IfpaWpprStandingDto? MapStanding(IfpaWpprStanding? standing) =>
        standing is null
            ? null
            : new IfpaWpprStandingDto(
                standing.RankingSystem,
                standing.CurrentRank,
                standing.LastMonthRank,
                standing.CurrentPoints,
                standing.ActivePoints,
                standing.RatingsRank,
                standing.RatingsValue,
                standing.TotalEventsAllTime,
                standing.TotalActiveEvents);

    private static string Attribution(string publicUrl) =>
        $"IFPA — {publicUrl}";
}
