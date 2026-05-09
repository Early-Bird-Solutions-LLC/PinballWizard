using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Landing;
using Xunit;

namespace PinballWizard.Scraper.Tests.Application.Landing;

public sealed class LandingServiceTests
{
    private readonly ISeedQuestionLoader _loader = Substitute.For<ISeedQuestionLoader>();
    private readonly LandingService _service;

    public LandingServiceTests()
    {
        _service = new LandingService(_loader, NullLogger<LandingService>.Instance);
    }

    // ── SeedQuestions surface ────────────────────────────────────────────────

    [Fact]
    public async Task GetLandingAsync_ReturnsQuestionsFromLoader()
    {
        var expected = new List<SeedQuestion>
        {
            new("slug-rules", "A rules question?", "Rules", "desc"),
            new("slug-valuation", "A valuation question?", "Valuation", "desc"),
        };

        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(expected.AsReadOnly());

        var response = await _service.GetLandingAsync(CancellationToken.None);

        Assert.Equal(2, response.SeedQuestions.Count);
        Assert.Equal("slug-rules", response.SeedQuestions[0].Slug);
        Assert.Equal("slug-valuation", response.SeedQuestions[1].Slug);
    }

    [Fact]
    public async Task GetLandingAsync_LoaderReturnedEmptyList_ResponseHasEmptySeedQuestions()
    {
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);

        var response = await _service.GetLandingAsync(CancellationToken.None);

        Assert.Empty(response.SeedQuestions);
    }

    // ── Placeholder fields are null (PR-L2 / PR-L3 fill these) ────────────

    [Fact]
    public async Task GetLandingAsync_FeaturedMachinesIsNull()
    {
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);

        var response = await _service.GetLandingAsync(CancellationToken.None);

        Assert.Null(response.FeaturedMachines);
    }

    [Fact]
    public async Task GetLandingAsync_SystemStatusIsNull()
    {
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);

        var response = await _service.GetLandingAsync(CancellationToken.None);

        Assert.Null(response.SystemStatus);
    }

    // ── Constructor null-checks ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLoader_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LandingService(null!, NullLogger<LandingService>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LandingService(_loader, null!));
    }

    // ── Loader call-through ──────────────────────────────────────────────────

    [Fact]
    public async Task GetLandingAsync_PassesCancellationTokenToLoader()
    {
        using var cts = new CancellationTokenSource();
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);

        await _service.GetLandingAsync(cts.Token);

        await _loader.Received(1).LoadAsync(cts.Token);
    }
}
