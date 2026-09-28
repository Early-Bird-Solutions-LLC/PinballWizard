using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Cli.Commands;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Http;
using PinballWizard.Infrastructure.Scraping.Kineticist;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Cli.Tests.Commands;

/// <summary>
/// Job-level behavior of <c>--sync-kineticist-tutorials</c> (issue #968): the
/// weekly ACA job must fail with a diagnosable message and a deliberate exit
/// code when the source refuses us — never an unhandled exception, and never a
/// green run that indexed nothing. Runs the real <see cref="KineticistTutorialsClient"/>
/// over the real <see cref="PolitenessGate"/>; only the wire and the RAG sinks are faked.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class SyncKineticistTutorialsCommandTests : IDisposable
{
    private const string BaseUrl = "https://www.kineticist.com";
    private const string SitemapUrl = BaseUrl + "/sitemap/news.xml";

    private const string SitemapXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
        <url><loc>https://www.kineticist.com/news/godzilla-pinball-tutorial</loc></url>
        <url><loc>https://www.kineticist.com/news/jaws-pinball-tutorial</loc></url>
        <url><loc>https://www.kineticist.com/news/brian-allen-interview</loc></url>
        </urlset>
        """;

    private const string GodzillaMd = """
        # Go, Go, Godzilla! Basic Strategy for a Modern Pinball Classic

        by [Noah Crable](/author/noah-crable) · May 1, 2026 · [Pinball Tutorial](/news/category/pinball-tutorial)

        Shoot the Tokyo lanes to light Building Frenzy.

        https://www.kineticist.com/news/godzilla-pinball-tutorial
        """;

    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly int _originalExitCode = Environment.ExitCode;

    public SyncKineticistTutorialsCommandTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        Environment.ExitCode = 0;
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        Environment.ExitCode = _originalExitCode;
        _stdout.Dispose();
        _stderr.Dispose();
    }

    [Fact]
    public async Task RunAsync_Persistent429OnDiscovery_FailsWithUrlAndReasonAfterTheBudget()
    {
        var wire = new StubWire().On(SitemapUrl, _ => Status(HttpStatusCode.TooManyRequests));
        var (services, indexer) = BuildServices(wire, new PolitenessOptions { Max429Streak = 1, RateLimitBackoffMs = 1_000 });

        // Must not throw: the old path surfaced "Unhandled exception: HttpRequestException ... 429".
        await SyncKineticistTutorialsCommand.RunAsync(services, CancellationToken.None);

        Assert.Equal(1, Environment.ExitCode);
        var stderr = _stderr.ToString();
        Assert.Contains("FAILED during discovery", stderr, StringComparison.Ordinal);
        Assert.Contains(SitemapUrl, stderr, StringComparison.Ordinal);
        Assert.Contains(nameof(PolitenessViolation.TooMany429Responses), stderr, StringComparison.Ordinal);
        // Budget of one re-send (Max429Streak = 1), then stop asking.
        Assert.Equal(2, wire.Count(SitemapUrl));
        await indexer.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_Discovery429WithRetryAfterThen200_WaitsAndIndexesTheTutorials()
    {
        var sitemapCalls = 0;
        var wire = new StubWire()
            .On(SitemapUrl, _ => ++sitemapCalls == 1
                ? Status(HttpStatusCode.TooManyRequests, r => r.Headers.TryAddWithoutValidation("Retry-After", "1"))
                : Body(SitemapXml, "application/xml"))
            .On($"{BaseUrl}/news/godzilla-pinball-tutorial.md", _ => Body(GodzillaMd, "text/markdown"))
            .On($"{BaseUrl}/news/jaws-pinball-tutorial.md", _ => Body(GodzillaMd.Replace("godzilla", "jaws", StringComparison.Ordinal), "text/markdown"));
        var (services, indexer) = BuildServices(wire, new PolitenessOptions { RequestDelayMs = 250 });

        var started = DateTimeOffset.UtcNow;
        await SyncKineticistTutorialsCommand.RunAsync(services, CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(2, wire.Count(SitemapUrl));
        Assert.True(elapsed >= TimeSpan.FromSeconds(1), $"Retry-After: 1 must be honored; run took {elapsed}.");
        Assert.Contains("indexed=2", _stdout.ToString(), StringComparison.Ordinal);
        await indexer.ReceivedWithAnyArgs(2).UpsertAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_BotChallengeOnArticle_StopsTheRunAndFailsWithRemediation()
    {
        var wire = new StubWire()
            .On(SitemapUrl, _ => Body(SitemapXml, "application/xml"))
            .On($"{BaseUrl}/news/godzilla-pinball-tutorial.md", _ => VercelChallenge())
            .On($"{BaseUrl}/news/jaws-pinball-tutorial.md", _ => VercelChallenge());
        var (services, _) = BuildServices(wire, new PolitenessOptions { RequestDelayMs = 250 });

        await SyncKineticistTutorialsCommand.RunAsync(services, CancellationToken.None);

        Assert.Equal(1, Environment.ExitCode);
        var stderr = _stderr.ToString();
        Assert.Contains(nameof(PolitenessViolation.BotChallenge), stderr, StringComparison.Ordinal);
        Assert.Contains($"{BaseUrl}/news/", stderr, StringComparison.Ordinal);
        Assert.Contains("allow the PinballWizard User-Agent", stderr, StringComparison.Ordinal);
        Assert.Contains("aborted", _stdout.ToString(), StringComparison.Ordinal);
        // One challenged article, then no further requests to the host.
        Assert.Equal(1, wire.Count($"{BaseUrl}/news/godzilla-pinball-tutorial.md") + wire.Count($"{BaseUrl}/news/jaws-pinball-tutorial.md"));
    }

    [Fact]
    public async Task RunAsync_SitemapWithNoTutorials_FailsInsteadOfReportingAnEmptySuccess()
    {
        const string noTutorials = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
            <url><loc>https://www.kineticist.com/news/brian-allen-interview</loc></url>
            </urlset>
            """;
        var wire = new StubWire().On(SitemapUrl, _ => Body(noTutorials, "application/xml"));
        var (services, _) = BuildServices(wire, new PolitenessOptions());

        await SyncKineticistTutorialsCommand.RunAsync(services, CancellationToken.None);

        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains("0 tutorial slugs discovered", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoTutorialMatchesTheCatalog_FailsBecauseNothingWasIndexed()
    {
        var wire = new StubWire()
            .On(SitemapUrl, _ => Body(SitemapXml, "application/xml"))
            .On($"{BaseUrl}/news/godzilla-pinball-tutorial.md", _ => Body(GodzillaMd, "text/markdown"))
            .On($"{BaseUrl}/news/jaws-pinball-tutorial.md", _ => Body(GodzillaMd, "text/markdown"));
        var (services, indexer) = BuildServices(wire, new PolitenessOptions { RequestDelayMs = 250 }, catalogKnowsTheGames: false);

        await SyncKineticistTutorialsCommand.RunAsync(services, CancellationToken.None);

        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains("0 of 2 discovered tutorials were indexed", _stderr.ToString(), StringComparison.Ordinal);
        await indexer.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_OneArticleServerError_CountsAFailureAndKeepsGoing()
    {
        var wire = new StubWire()
            .On(SitemapUrl, _ => Body(SitemapXml, "application/xml"))
            .On($"{BaseUrl}/news/godzilla-pinball-tutorial.md", _ => Status(HttpStatusCode.ServiceUnavailable))
            .On($"{BaseUrl}/news/jaws-pinball-tutorial.md", _ => Body(GodzillaMd.Replace("godzilla", "jaws", StringComparison.Ordinal), "text/markdown"));
        var (services, indexer) = BuildServices(wire, new PolitenessOptions { RequestDelayMs = 250 });

        await SyncKineticistTutorialsCommand.RunAsync(services, CancellationToken.None);

        // The 503 is a failure (exit 1), not "no content"; the other article still indexes.
        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains("fetching article 'godzilla-pinball-tutorial' failed: HTTP 503", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("indexed=1", _stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("failed=1", _stdout.ToString(), StringComparison.Ordinal);
        await indexer.ReceivedWithAnyArgs(1).UpsertAsync(default!, default!, default!, default);
    }

    private static (IServiceProvider Services, IRagIndexer Indexer) BuildServices(
        StubWire wire, PolitenessOptions politeness, bool catalogKnowsTheGames = true)
    {
        politeness.RespectRobotsTxt = false;
        var politenessOptions = Options.Create(politeness);
        var gate = new PolitenessGate(
            new RobotsTxtCache(new HttpClient(new StubWire()), politenessOptions, NullLogger<RobotsTxtCache>.Instance),
            new DefaultPerSourcePolitenessResolver(politenessOptions),
            NullLogger<PolitenessGate>.Instance);

        var client = new KineticistTutorialsClient(
            // As registered in production: AddPoliteResilienceHandler stamps requests
            // so the base re-sends after a 429 through the gate.
            new HttpClient(new GateOwnsRateLimitHandler { InnerHandler = wire }) { BaseAddress = new Uri(BaseUrl) },
            gate,
            politenessOptions,
            Options.Create(new KineticistOptions { BaseUrl = BaseUrl }),
            NullLogger<KineticistTutorialsClient>.Instance);

        var chunker = Substitute.For<IChunker>();
        chunker.Chunk(Arg.Any<ExtractedDocument>(), Arg.Any<ChunkRequest>(), Arg.Any<CancellationToken>())
            .Returns([new Chunk(0, "Shoot the Tokyo lanes.", "Strategy", 1, 1, 6)]);

        var titleLookups = Substitute.For<IMachineTitleLookupRepository>();
        titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => !catalogKnowsTheGames ? null : new MachineTitleLookup
            {
                Id = call.Arg<string>(),
                PartitionKey = call.Arg<string>(),
                OpdbIds = ["GrXzD-MjBPX"],
                Manufacturers = ["Stern"],
            });

        var indexer = Substitute.For<IRagIndexer>();
        indexer.UpsertAsync(Arg.Any<ChunkRequest>(), Arg.Any<IReadOnlyList<Chunk>>(), Arg.Any<RagIndexerOptions>(), Arg.Any<CancellationToken>())
            .Returns(new IndexUpsertResult(1, []));

        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(client)
            .AddSingleton(new KineticistTutorialsSynthesizer(chunker, NullLogger<KineticistTutorialsSynthesizer>.Instance))
            .AddSingleton(titleLookups)
            .AddSingleton(indexer)
            .AddSingleton(Options.Create(new KineticistOptions { BaseUrl = BaseUrl }))
            .BuildServiceProvider();

        return (services, indexer);
    }

    // The live www.kineticist.com response since 2026-08-23: 429, no Retry-After.
    private static HttpResponseMessage VercelChallenge() =>
        Status(HttpStatusCode.TooManyRequests, r => r.Headers.TryAddWithoutValidation("x-vercel-mitigated", "challenge"));

    private static HttpResponseMessage Body(string body, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage Status(HttpStatusCode status, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(status.ToString()) };
        configure?.Invoke(response);
        return response;
    }

    private sealed class StubWire : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _requests = [];

        public StubWire On(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _routes[url] = respond;
            return this;
        }

        public int Count(string url) => _requests.Count(r => string.Equals(r, url, StringComparison.OrdinalIgnoreCase));

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to the HttpClient caller via SendAsync; the caller disposes it.")]
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            _requests.Add(url);
            return Task.FromResult(_routes.TryGetValue(url, out var respond)
                ? respond(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
