using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.MatchPlay;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.MatchPlay;

public sealed class MatchPlayRatingsProviderTests
{
    private const string ExportUrl =
        "https://mp-ratings.sfo3.cdn.digitaloceanspaces.com/latest-ratings.csv";

    private const string SampleCsv =
        "Name,User ID,IFPA ID,Rating,RD,Lower Bound,Last Rating Period,Rating class\n" +
        "Ada Lovelace,101,42,1850.5,45.2,1760.1,2026-11-14,3\n" +
        "Bob Builder,202,7,1600.0,50.0,1500.0,2026-11-14,5\n" +
        "Ada Extra,303,99,1900.0,40.0,1820.0,2026-11-14,2\n";

    [Fact]
    public async Task Search_ByNameFragment_OrdersByRatingDescending()
    {
        using var harness = NewProvider(SampleCsv);

        var hits = await harness.Provider.SearchAsync("Ada");

        Assert.Equal(2, hits.Count);
        Assert.Equal("Ada Extra", hits[0].Name);
        Assert.Equal("Ada Lovelace", hits[1].Name);
    }

    [Fact]
    public async Task Search_ByUserId_AndIfpaId()
    {
        using var harness = NewProvider(SampleCsv);

        var byUser = await harness.Provider.SearchAsync("101");
        var byIfpa = await harness.Provider.SearchAsync("42");

        Assert.Single(byUser);
        Assert.Equal("Ada Lovelace", byUser[0].Name);
        Assert.Single(byIfpa);
        Assert.Equal(101, byIfpa[0].UserId);
    }

    [Fact]
    public async Task Compare_ResolvesBothSides()
    {
        using var harness = NewProvider(SampleCsv);

        var (left, right) = await harness.Provider.CompareAsync("Ada Lovelace", "Bob");

        Assert.Equal("Ada Lovelace", left!.Name);
        Assert.Equal("Bob Builder", right!.Name);
    }

    [Fact]
    public async Task GetBestMatch_PrefersExactNameOverHigherRatedContains()
    {
        var csv =
            "Name,User ID,IFPA ID,Rating,RD,Lower Bound,Last Rating Period,Rating class\n" +
            "Ada,101,42,1500.0,45.2,1400.0,2026-11-14,5\n" +
            "Ada Extra,303,99,1900.0,40.0,1820.0,2026-11-14,2\n";
        using var harness = NewProvider(csv);

        // Contains("Ada") hits both; without exact preference the higher rating wins.
        var best = await harness.Provider.GetBestMatchAsync("Ada");

        Assert.Equal("Ada", best!.Name);
        Assert.Equal(101, best.UserId);
    }

    [Fact]
    public async Task GetBestMatch_ExactName_NotLimitedToTopContainsHits()
    {
        // Six higher-rated "Ada*" substring hits would fill SearchAsync(limit:5)
        // and hide exact "Ada" if GetBestMatch only scanned that window.
        var lines = new List<string>
        {
            "Name,User ID,IFPA ID,Rating,RD,Lower Bound,Last Rating Period,Rating class",
            "Ada,1,1,1000.0,50.0,900.0,2026-11-14,9",
        };
        for (var i = 0; i < 6; i++)
        {
            var rating = 2000 + i;
            lines.Add($"Ada Extra{i},{10 + i},{100 + i},{rating}.0,40.0,{rating - 80}.0,2026-11-14,1");
        }

        using var harness = NewProvider(string.Join('\n', lines));

        var best = await harness.Provider.GetBestMatchAsync("Ada");

        Assert.Equal("Ada", best!.Name);
        Assert.Equal(1, best.UserId);
    }

    [Fact]
    public async Task EnsureIndex_CachesUntilTtl_SingleDownload()
    {
        using var harness = NewProvider(SampleCsv);

        _ = await harness.Provider.SearchAsync("Ada");
        _ = await harness.Provider.SearchAsync("Bob");

        Assert.Equal(1, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task Download_HttpFailure_Propagates()
    {
        using var harness = NewProvider(
            SampleCsv,
            statusCode: System.Net.HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => harness.Provider.SearchAsync("Ada"));
    }

    private static ProviderHarness NewProvider(
        string csv,
        System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
    {
        var handler = new CountingHandler(csv, statusCode);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("MatchPlayRatingsCdn").Returns(client);
        var provider = new MatchPlayRatingsProvider(
            factory,
            Options.Create(new MatchPlayOptions
            {
                RatingsExportUrl = ExportUrl,
                RatingsCacheHours = 12,
            }),
            NullLogger<MatchPlayRatingsProvider>.Instance);
        return new ProviderHarness(provider, client, handler);
    }

    private sealed class ProviderHarness : IDisposable
    {
        public MatchPlayRatingsProvider Provider { get; }
        public CountingHandler Handler { get; }
        private readonly HttpClient _client;

        public ProviderHarness(
            MatchPlayRatingsProvider provider,
            HttpClient client,
            CountingHandler handler)
        {
            Provider = provider;
            _client = client;
            Handler = handler;
        }

        public void Dispose()
        {
            Provider.Dispose();
            _client.Dispose();
            Handler.Dispose();
        }
    }

    // Owns responses created for HttpClient so CodeQL sees Dispose on every
    // HttpResponseMessage allocated by the handler (client using-dispose is
    // not visible to the analyzer at the creation site). Same pattern as
    // MatchPlayClientTests.RecordingHandler.
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly System.Net.HttpStatusCode _status;
        private readonly List<HttpResponseMessage> _created = [];

        public int RequestCount { get; private set; }

        public CountingHandler(string body, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _body = body;
            _status = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            };
            _created.Add(response);
            return Task.FromResult(response);
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
