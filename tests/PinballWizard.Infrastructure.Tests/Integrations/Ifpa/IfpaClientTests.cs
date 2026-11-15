using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.Ifpa;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Ifpa;

public sealed class IfpaClientTests : IDisposable
{
    private readonly RecordingHandler _handler = new();
    private readonly HttpClient _http;
    private readonly IfpaClient _client;

    public IfpaClientTests()
    {
        _http = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://api.ifpapinball.com/"),
        };
        var options = Options.Create(new IfpaOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://api.ifpapinball.com/",
        });
        _client = new IfpaClient(_http, options, NullLogger<IfpaClient>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task SearchPlayersAsync_ParsesResultsArray_AndAppendsApiKey()
    {
        _handler.Map(
            "https://api.ifpapinball.com/player/search?name=Sharpe&country=US&stateprov=IL&api_key=test-key",
            """
            {"count":1,"total_results":1,"results":[{"player_id":4,"first_name":"Josh","last_name":"Sharpe","country_name":"United States","country_code":"US","city":"Chicago","stateprov":"IL","wppr_rank":10,"rating_value":1234.56}]}
            """);

        var hits = await _client.SearchPlayersAsync("Sharpe", "US", "IL");

        Assert.Single(hits);
        Assert.Equal(4, hits[0].PlayerId);
        Assert.Equal("Josh", hits[0].FirstName);
        Assert.Equal(10, hits[0].WpprRank);
    }

    [Fact]
    public async Task SearchPlayersAsync_ParsesSingleObjectResults()
    {
        _handler.Map(
            "https://api.ifpapinball.com/player/search?name=Sharpe&api_key=test-key",
            """
            {"count":1,"total_results":1,"results":{"player_id":4,"first_name":"Josh","last_name":"Sharpe","wppr_rank":10}}
            """);

        var hits = await _client.SearchPlayersAsync("Sharpe");

        Assert.Single(hits);
        Assert.Equal(4, hits[0].PlayerId);
    }

    [Fact]
    public async Task GetPlayerAsync_ParsesEnvelopeWithMainStanding()
    {
        _handler.Map(
            "https://api.ifpapinball.com/player/4?api_key=test-key",
            """
            {"player":{"player_id":4,"first_name":"Josh","last_name":"Sharpe","initials":"JLS","city":"Chicago","stateprov":"IL","country_name":"United States","country_code":"US","ifpa_registered":true,"player_stats":{"system":{"MAIN":{"current_rank":10,"last_month_rank":11,"current_points":1710.11,"active_points":1500,"ratings_rank":15,"ratings_value":2311,"total_events_all_time":151,"total_active_events":40}}}}}
            """);

        var player = await _client.GetPlayerAsync(4);

        Assert.NotNull(player);
        Assert.Equal("Josh", player!.FirstName);
        Assert.Equal(10, player.PlayerStats!.System!.Main!.CurrentRank);
        Assert.Equal(1710.11, player.PlayerStats.System.Main.CurrentPoints);
    }

    [Fact]
    public async Task GetPlayerAsync_404_ReturnsNull()
    {
        _handler.MapStatus(
            "https://api.ifpapinball.com/player/404?api_key=test-key",
            HttpStatusCode.NotFound);

        Assert.Null(await _client.GetPlayerAsync(404));
    }

    [Fact]
    public async Task GetPlayerAsync_NonSuccess_ThrowsWithoutApiKeyInMessage()
    {
        _handler.MapStatus(
            "https://api.ifpapinball.com/player/4?api_key=test-key",
            HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.GetPlayerAsync(4));

        Assert.DoesNotContain("test-key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("player/4", ex.Message, StringComparison.Ordinal);
    }

    // Owns responses created for HttpClient so CodeQL sees Dispose on every
    // HttpResponseMessage allocated by the handler.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _map =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<HttpResponseMessage> _created = [];

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
                return Task.FromResult(Track(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("", Encoding.UTF8, "application/json"),
                }));
            }

            return Task.FromResult(Track(new HttpResponseMessage(entry.Status)
            {
                Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
            }));
        }

        private HttpResponseMessage Track(HttpResponseMessage response)
        {
            _created.Add(response);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _created)
                {
                    response.Dispose();
                }

                _created.Clear();
            }

            base.Dispose(disposing);
        }
    }
}
