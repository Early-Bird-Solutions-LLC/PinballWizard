using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Infrastructure.Integrations.MatchPlay;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.MatchPlay;

public sealed class MatchPlayClientTests : IDisposable
{
    private readonly RecordingHandler _handler = new();
    private readonly MatchPlayClient _client;

    public MatchPlayClientTests()
    {
        var http = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://api.matchplay.events/"),
        };
        _client = new MatchPlayClient(http, NullLogger<MatchPlayClient>.Instance);
    }

    public void Dispose() => _handler.Dispose();

    [Fact]
    public async Task SearchTournamentsAsync_ParsesDataArray()
    {
        _handler.Map(
            "https://api.matchplay.events/api/search?type=tournaments&query=INDISC",
            """
            {"data":[{"tournamentId":42,"name":"INDISC","status":"started","type":"group_matchplay","startUtc":"2026-11-01T12:00:00.000000Z"}]}
            """);

        var hits = await _client.SearchTournamentsAsync("INDISC");

        Assert.Single(hits);
        Assert.Equal(42, hits[0].TournamentId);
        Assert.Equal("INDISC", hits[0].Name);
        Assert.Equal("started", hits[0].Status);
    }

    [Fact]
    public async Task GetTournamentAsync_IncludePlayers_ParsesEnvelope()
    {
        _handler.Map(
            "https://api.matchplay.events/api/tournaments/42?includePlayers=true",
            """
            {"data":{"tournamentId":42,"name":"INDISC","status":"started","players":[{"playerId":7,"name":"Ada","ifpaId":99}]}}
            """);

        var t = await _client.GetTournamentAsync(42, includePlayers: true);

        Assert.NotNull(t);
        Assert.Equal("Ada", t!.Players![0].Name);
        Assert.Equal(99, t.Players[0].IfpaId);
    }

    [Fact]
    public async Task GetStandingsAsync_ParsesArray()
    {
        _handler.Map(
            "https://api.matchplay.events/api/tournaments/42/standings",
            """
            [{"playerId":7,"position":1,"points":"10.00","gamesPlayed":3}]
            """);

        var rows = await _client.GetStandingsAsync(42);

        Assert.Single(rows);
        Assert.Equal(1, rows[0].Position);
        Assert.Equal("10.00", rows[0].Points);
    }

    [Fact]
    public async Task GetTournamentAsync_404_ReturnsNull()
    {
        _handler.MapStatus(
            "https://api.matchplay.events/api/tournaments/404?includePlayers=true",
            HttpStatusCode.NotFound);

        Assert.Null(await _client.GetTournamentAsync(404, includePlayers: true));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public void Map(string url, string json) =>
            _map[url] = (HttpStatusCode.OK, json);

        public void MapStatus(string url, HttpStatusCode status) =>
            _map[url] = (status, "");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.ToString();
            if (!_map.TryGetValue(key, out var entry))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(entry.Status)
            {
                Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
            });
        }

        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}
