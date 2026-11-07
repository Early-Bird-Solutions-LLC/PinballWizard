using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.Kineticist;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Kineticist;

/// <summary>
/// Unit tests for <see cref="KineticistApiClient"/>: parsing the OPDB-keyed
/// game detail (the edition <c>opdb_id</c>s that join to our catalog), 404 →
/// null, and title-search parsing. Routes through a stub
/// <see cref="HttpMessageHandler"/> — no live network. Response shapes mirror
/// the live API verified 2026-06-26.
/// </summary>
public sealed class KineticistApiClientTests : IDisposable
{
    private readonly StubHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly KineticistApiClient _client;

    public KineticistApiClientTests()
    {
        _httpClient = new HttpClient(_handler);
        var options = Options.Create(new KineticistOptions
        {
            ApiBaseUrl = "https://www.kineticist.com/api/v1",
            ApiKey = "ki_live_test",
        });

        _client = new KineticistApiClient(_httpClient, options, NullLogger<KineticistApiClient>.Instance);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task GetGameBySlugAsync_ParsesAllEditionOpdbIds()
    {
        // A game with multiple editions — the rulesheet must link to every one.
        _handler.SetResponse("/api/v1/games/monster-bash", HttpStatusCode.OK, """
        {
          "object":"game","id":"gm_x",
          "data":{
            "name":"Monster Bash","slug":"monster-bash","opdb_id":"r3EW",
            "editions":[
              {"opdb_id":"Gr3EW-MD3Nj","name":"Monster Bash"},
              {"opdb_id":"Gr3EW-M3dBn","name":"Monster Bash (Remake)"}
            ]
          },"links":{}
        }
        """);

        var match = await _client.GetGameBySlugAsync("monster-bash", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("monster-bash", match!.Slug);
        Assert.Equal("Monster Bash", match.Name);
        Assert.Equal(["Gr3EW-MD3Nj", "Gr3EW-M3dBn"], match.EditionOpdbIds);
    }

    [Fact]
    public async Task GetGameBySlugAsync_NotFound_ReturnsNull()
    {
        _handler.SetResponse("/api/v1/games/does-not-exist", HttpStatusCode.NotFound, "");

        var match = await _client.GetGameBySlugAsync("does-not-exist", CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task GetGameBySlugAsync_NoEditions_ReturnsNull()
    {
        // A resolvable game that carries no editions cannot be joined to a
        // machine — treat as unresolved (the tutorial is skipped, not mis-linked).
        _handler.SetResponse("/api/v1/games/edition-less", HttpStatusCode.OK, """
        {"object":"game","id":"gm_y","data":{"name":"Edgeless","slug":"edition-less","opdb_id":"zzzz","editions":[]},"links":{}}
        """);

        var match = await _client.GetGameBySlugAsync("edition-less", CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task GetGameBySlugAsync_SendsBearerAuth()
    {
        _handler.SetResponse("/api/v1/games/monster-bash", HttpStatusCode.OK, """
        {"object":"game","id":"gm_x","data":{"name":"Monster Bash","slug":"monster-bash","editions":[{"opdb_id":"Gr3EW-MD3Nj","name":"Monster Bash"}]},"links":{}}
        """);

        await _client.GetGameBySlugAsync("monster-bash", CancellationToken.None);

        var auth = _handler.LastRequest!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("ki_live_test", auth.Parameter);
    }

    [Fact]
    public async Task SearchGamesAsync_ParsesNameAndSlug()
    {
        _handler.SetResponse("/api/v1/games?q=mata%20hari&limit=5", HttpStatusCode.OK, """
        {"object":"list","data":[{"name":"Mata Hari","slug":"mata-hari"}]}
        """);

        var hits = await _client.SearchGamesAsync("mata hari", 5, CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal("Mata Hari", hit.Name);
        Assert.Equal("mata-hari", hit.Slug);
    }

    // ── Tier B guide deep-links (ADR-0043; shape verified 2026-11-07) ─────

    [Fact]
    public async Task GetGuideLinksAsync_ParsesTutorialRulesAndPrimerFields()
    {
        // Mirrors the keyed Godzilla probe: flat fields on data, not a nested
        // rules_links array. Primer may be null.
        _handler.SetResponse("/api/v1/games/godzilla-2021", HttpStatusCode.OK, """
        {
          "object":"game","id":"gm_gz",
          "data":{
            "name":"Godzilla","slug":"godzilla-2021","opdb_id":"G33YF",
            "full_tutorial_link":"https://www.kineticist.com/news/godzilla-pinball-tutorial",
            "game_rules_link":"https://pinballrulesheets.com/stern/stern-godzilla-rulesheet.html",
            "pinball_primer_url":null,
            "editions":[{"opdb_id":"G33YF-Mxxxx","name":"Godzilla Pro"}]
          },"links":{}
        }
        """);

        var links = await _client.GetGuideLinksAsync("godzilla-2021", CancellationToken.None);

        Assert.NotNull(links);
        Assert.Equal("godzilla-2021", links!.Slug);
        Assert.Equal("Godzilla", links.Name);
        Assert.Equal("G33YF", links.OpdbId);
        Assert.Equal("https://www.kineticist.com/news/godzilla-pinball-tutorial", links.FullTutorialLink);
        Assert.Equal("https://pinballrulesheets.com/stern/stern-godzilla-rulesheet.html", links.GameRulesLink);
        Assert.Null(links.PinballPrimerUrl);
        Assert.True(links.HasAnyLink);
        Assert.Equal(links.FullTutorialLink, links.PrimaryAttributionUrl);
    }

    [Fact]
    public async Task GetGuideLinksAsync_AcceptsOpdbIdAsIdOrSlug()
    {
        // Live API accepts OPDB group / machine ids on /games/{idOrSlug}.
        _handler.SetResponse("/api/v1/games/G33YF", HttpStatusCode.OK, """
        {
          "object":"game","id":"gm_gz",
          "data":{
            "name":"Godzilla","slug":"godzilla-2021","opdb_id":"G33YF",
            "full_tutorial_link":"https://www.kineticist.com/news/godzilla-pinball-tutorial",
            "game_rules_link":null,
            "pinball_primer_url":null,
            "editions":[]
          },"links":{}
        }
        """);

        var links = await _client.GetGuideLinksAsync("G33YF", CancellationToken.None);

        Assert.NotNull(links);
        Assert.Equal("godzilla-2021", links!.Slug);
        Assert.Equal("https://www.kineticist.com/news/godzilla-pinball-tutorial", links.FullTutorialLink);
    }

    [Fact]
    public async Task GetGuideLinksAsync_NoLinkFields_ReturnsNull()
    {
        // Fail closed: a resolvable game with empty deep-link fields is unresolved.
        _handler.SetResponse("/api/v1/games/no-links", HttpStatusCode.OK, """
        {"object":"game","id":"gm_z","data":{"name":"No Links","slug":"no-links","opdb_id":"zzzz","editions":[],"full_tutorial_link":null,"game_rules_link":null,"pinball_primer_url":null},"links":{}}
        """);

        var links = await _client.GetGuideLinksAsync("no-links", CancellationToken.None);

        Assert.Null(links);
    }

    [Fact]
    public async Task GetGuideLinksAsync_DoesNotLogApiKey()
    {
        // Guard: secret must never appear in log output. Uses a capturing logger.
        var capture = new CapturingLogger();
        var client = new KineticistApiClient(
            _httpClient,
            Options.Create(new KineticistOptions
            {
                ApiBaseUrl = "https://www.kineticist.com/api/v1",
                ApiKey = "ki_live_super_secret_should_not_log",
            }),
            capture);

        _handler.SetResponse("/api/v1/games/godzilla-2021", HttpStatusCode.OK, """
        {"object":"game","id":"gm_gz","data":{"name":"Godzilla","slug":"godzilla-2021","full_tutorial_link":"https://www.kineticist.com/news/godzilla-pinball-tutorial","editions":[{"opdb_id":"G33YF-Mxxxx"}]},"links":{}}
        """);

        await client.GetGuideLinksAsync("godzilla-2021", CancellationToken.None);

        Assert.DoesNotContain(
            capture.Messages,
            m => m.Contains("ki_live_super_secret_should_not_log", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger<KineticistApiClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // Minimal stub: maps an exact request PathAndQuery to a canned response.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new();
        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(string pathAndQuery, HttpStatusCode status, string body)
            => _responses[pathAndQuery] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var key = request.RequestUri!.PathAndQuery;
            if (_responses.TryGetValue(key, out var r))
            {
                return Task.FromResult(new HttpResponseMessage(r.Status)
                {
                    Content = new StringContent(r.Body, Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json"),
            });
        }
    }
}
