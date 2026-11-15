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
        var provider = NewProvider(SampleCsv);

        var hits = await provider.SearchAsync("Ada");

        Assert.Equal(2, hits.Count);
        Assert.Equal("Ada Extra", hits[0].Name);
        Assert.Equal("Ada Lovelace", hits[1].Name);
    }

    [Fact]
    public async Task Search_ByUserId_AndIfpaId()
    {
        var provider = NewProvider(SampleCsv);

        var byUser = await provider.SearchAsync("101");
        var byIfpa = await provider.SearchAsync("42");

        Assert.Single(byUser);
        Assert.Equal("Ada Lovelace", byUser[0].Name);
        Assert.Single(byIfpa);
        Assert.Equal(101, byIfpa[0].UserId);
    }

    [Fact]
    public async Task Compare_ResolvesBothSides()
    {
        var provider = NewProvider(SampleCsv);

        var (left, right) = await provider.CompareAsync("Ada Lovelace", "Bob");

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
        var provider = NewProvider(csv);

        // Contains("Ada") hits both; without exact preference the higher rating wins.
        var best = await provider.GetBestMatchAsync("Ada");

        Assert.Equal("Ada", best!.Name);
        Assert.Equal(101, best.UserId);
    }

    [Fact]
    public async Task EnsureIndex_CachesUntilTtl_SingleDownload()
    {
        var handler = new CountingHandler(SampleCsv);
        var provider = NewProvider(SampleCsv, handler);

        _ = await provider.SearchAsync("Ada");
        _ = await provider.SearchAsync("Bob");

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Download_HttpFailure_Propagates()
    {
        var handler = new CountingHandler(SampleCsv, statusCode: System.Net.HttpStatusCode.NotFound);
        var provider = NewProvider(SampleCsv, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync("Ada"));
    }

    private static MatchPlayRatingsProvider NewProvider(string csv, CountingHandler? handler = null)
    {
        handler ??= new CountingHandler(csv);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("MatchPlayRatingsCdn").Returns(new HttpClient(handler));
        return new MatchPlayRatingsProvider(
            factory,
            Options.Create(new MatchPlayOptions
            {
                RatingsExportUrl = ExportUrl,
                RatingsCacheHours = 12,
            }),
            NullLogger<MatchPlayRatingsProvider>.Instance);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly System.Net.HttpStatusCode _status;

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
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
