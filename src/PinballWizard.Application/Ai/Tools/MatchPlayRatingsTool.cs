using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Tournaments;

namespace PinballWizard.Application.Ai.Tools;

// Wizard tools over Match Play Ratings CDN (ADR-0063).
// Fail closed → null. Citation via AttributionUrl on every payload.
public sealed class MatchPlayRatingsTool
{
    internal const string SearchToolTag = "searchMatchPlayRatings";
    internal const string CompareToolTag = "compareMatchPlayRatings";

    private readonly IMatchPlayRatingsProvider? _provider;
    private readonly ILogger<MatchPlayRatingsTool> _logger;

    public MatchPlayRatingsTool(
        ILogger<MatchPlayRatingsTool> logger,
        IMatchPlayRatingsProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _provider = provider;
    }

    [Description("Search Match Play Ratings (Glicko) by player name or numeric IFPA/User id. Returns rating, RD, lower bound, and IFPA id when known. Data comes from the Match Play Ratings CDN export (not IFPA WPPR). Attribute Match Play Ratings. Returns null when unavailable.")]
    public async Task<MatchPlayRatingsSearchDto?> SearchMatchPlayRatingsAsync(
        [Description("Player name fragment or numeric IFPA / Match Play user id.")] string query,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(SearchToolTag) || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var hits = await _provider!.SearchAsync(query.Trim(), limit: 10, cancellationToken)
                .ConfigureAwait(false);
            if (hits.Count == 0)
            {
                return null;
            }

            return new MatchPlayRatingsSearchDto(
                query.Trim(),
                _provider.AttributionUrl,
                _provider.ExportUrl,
                Attribution(_provider.AttributionUrl),
                hits.Select(MapHit).ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Broad catch is intentional (Invariant #17 / OBS-01 sibling of
        // MatchPlayTournamentTool.RunAsync): Wizard tools fail closed to null
        // on any provider/mapping fault so the turn continues. Narrowing to
        // HttpRequestException alone would let InvalidOperationException /
        // ArgumentException escape and abort the answer.
        catch (Exception ex)
        {
            return FailSearch(ex, query);
        }
        finally
        {
            sw.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", SearchToolTag));
        }
    }

    [Description("Compare two players' Match Play Ratings (Glicko). Pass each as a name fragment or IFPA/user id. Returns both sides when resolved. Attribute Match Play Ratings. Not IFPA WPPR. Returns null when the provider is unavailable.")]
    public async Task<MatchPlayRatingsCompareDto?> CompareMatchPlayRatingsAsync(
        [Description("First player name or id.")] string leftQuery,
        [Description("Second player name or id.")] string rightQuery,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureProvider(CompareToolTag)
            || string.IsNullOrWhiteSpace(leftQuery)
            || string.IsNullOrWhiteSpace(rightQuery))
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var (left, right) = await _provider!
                .CompareAsync(leftQuery.Trim(), rightQuery.Trim(), cancellationToken)
                .ConfigureAwait(false);

            if (left is null && right is null)
            {
                return null;
            }

            return new MatchPlayRatingsCompareDto(
                leftQuery.Trim(),
                rightQuery.Trim(),
                _provider.AttributionUrl,
                _provider.ExportUrl,
                Attribution(_provider.AttributionUrl),
                left is null ? null : MapHit(left),
                right is null ? null : MapHit(right));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // See SearchMatchPlayRatingsAsync — fail-closed broad catch is the
        // Wizard tool contract (matches MatchPlayTournamentTool.RunAsync).
        catch (Exception ex)
        {
            return FailCompare(ex, leftQuery, rightQuery);
        }
        finally
        {
            sw.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", CompareToolTag));
        }
    }

    private bool EnsureProvider(string toolTag)
    {
        if (_provider is not null)
        {
            return true;
        }

        _logger.LogDebug(
            "MatchPlayRatingsTool: provider not registered — returning null from {Tool}",
            toolTag);
        return false;
    }

    private static MatchPlayRatingHitDto MapHit(MatchPlayRatingRow r) =>
        new(r.Name, r.UserId, r.IfpaId, r.Rating, r.Rd, r.LowerBound, r.LastRatingPeriod, r.RatingClass);

    private static string Attribution(string url) => $"Match Play Ratings — {url}";

    private MatchPlayRatingsSearchDto? FailSearch(Exception ex, string query)
    {
        PinballWizardTelemetry.AiToolErrors.Add(
            1, new KeyValuePair<string, object?>("tool", SearchToolTag));
        _logger.LogWarning(ex, "MatchPlayRatingsTool: search failed query={Query}", query);
        return null;
    }

    private MatchPlayRatingsCompareDto? FailCompare(Exception ex, string left, string right)
    {
        PinballWizardTelemetry.AiToolErrors.Add(
            1, new KeyValuePair<string, object?>("tool", CompareToolTag));
        _logger.LogWarning(
            ex,
            "MatchPlayRatingsTool: compare failed left={Left} right={Right}",
            left,
            right);
        return null;
    }
}
