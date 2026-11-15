using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Tournaments;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// Loads Match Play Ratings from the public CDN CSV and serves search/compare
// from an in-process TTL cache (ADR-0063). Fail closed on empty/malformed export.
internal sealed class MatchPlayRatingsProvider : IMatchPlayRatingsProvider, IDisposable
{
    internal const string DefaultAttributionUrl =
        "https://docs.matchplay.events/additional-features/match-play-ratings";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MatchPlayOptions _options;
    private readonly ILogger<MatchPlayRatingsProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile RatingsIndex? _index;
    private bool _disposed;

    public MatchPlayRatingsProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<MatchPlayOptions> options,
        ILogger<MatchPlayRatingsProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string AttributionUrl => DefaultAttributionUrl;

    public string ExportUrl =>
        string.IsNullOrWhiteSpace(_options.RatingsExportUrl)
            ? "https://mp-ratings.sfo3.cdn.digitaloceanspaces.com/latest-ratings.csv"
            : _options.RatingsExportUrl;

    public async Task<IReadOnlyList<MatchPlayRatingRow>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, 25);

        var index = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        var needle = query.Trim();

        if (int.TryParse(needle, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            var byId = new List<MatchPlayRatingRow>(2);
            if (index.ByUserId.TryGetValue(id, out var userRow))
            {
                byId.Add(userRow);
            }

            if (index.ByIfpaId.TryGetValue(id, out var ifpaRow)
                && !byId.Exists(r => r.UserId == ifpaRow.UserId))
            {
                byId.Add(ifpaRow);
            }

            if (byId.Count > 0)
            {
                return byId;
            }
        }

        return index.Rows
            .Where(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Rating)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public async Task<MatchPlayRatingRow?> GetBestMatchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var needle = query.Trim();
        var index = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);

        // Exact name over the full index — not limited to SearchAsync's top-N
        // contains window (short queries like "Ada" otherwise lose the exact
        // row among higher-rated "Adam"/"Ada Extra"/… hits).
        if (index.ByExactName.TryGetValue(needle, out var exact))
        {
            return exact;
        }

        var hits = await SearchAsync(needle, limit: 5, cancellationToken).ConfigureAwait(false);
        return hits.Count == 0 ? null : hits[0];
    }

    public async Task<(MatchPlayRatingRow? Left, MatchPlayRatingRow? Right)> CompareAsync(
        string leftQuery,
        string rightQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftQuery);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightQuery);

        var leftTask = GetBestMatchAsync(leftQuery, cancellationToken);
        var rightTask = GetBestMatchAsync(rightQuery, cancellationToken);
        await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);
        return (await leftTask.ConfigureAwait(false), await rightTask.ConfigureAwait(false));
    }

    private async Task<RatingsIndex> EnsureIndexAsync(CancellationToken cancellationToken)
    {
        var current = _index;
        if (current is not null && DateTimeOffset.UtcNow < current.ExpiresAtUtc)
        {
            return current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _index;
            if (current is not null && DateTimeOffset.UtcNow < current.ExpiresAtUtc)
            {
                return current;
            }

            var csv = await DownloadCsvAsync(cancellationToken).ConfigureAwait(false);
            var rows = MatchPlayRatingsCsvParser.Parse(csv);
            var byUser = new Dictionary<int, MatchPlayRatingRow>(rows.Count);
            var byIfpa = new Dictionary<int, MatchPlayRatingRow>();
            var byExactName = new Dictionary<string, MatchPlayRatingRow>(
                rows.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                byUser[row.UserId] = row;
                if (row.IfpaId is > 0)
                {
                    byIfpa[row.IfpaId.Value] = row;
                }

                // Duplicate exact names: keep the higher rating.
                if (!byExactName.TryGetValue(row.Name, out var existing)
                    || row.Rating > existing.Rating)
                {
                    byExactName[row.Name] = row;
                }
            }

            var ttl = TimeSpan.FromHours(Math.Max(1, _options.RatingsCacheHours));
            var index = new RatingsIndex(rows, byUser, byIfpa, byExactName, DateTimeOffset.UtcNow.Add(ttl));
            _index = index;
            _logger.LogInformation(
                "MatchPlayRatings: indexed {Count} row(s) from {Url}; cache TTL {TtlHours}h",
                rows.Count,
                ExportUrl,
                ttl.TotalHours);
            return index;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> DownloadCsvAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(ExportUrl, UriKind.Absolute, out var url))
        {
            throw new InvalidOperationException(
                $"MatchPlay:RatingsExportUrl must be an absolute URL; got '{ExportUrl}'.");
        }

        var client = _httpClientFactory.CreateClient("MatchPlayRatingsCdn");
        _logger.LogInformation("MatchPlayRatings: downloading CDN export {Url}", url);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private sealed record RatingsIndex(
        IReadOnlyList<MatchPlayRatingRow> Rows,
        Dictionary<int, MatchPlayRatingRow> ByUserId,
        Dictionary<int, MatchPlayRatingRow> ByIfpaId,
        Dictionary<string, MatchPlayRatingRow> ByExactName,
        DateTimeOffset ExpiresAtUtc);
}
