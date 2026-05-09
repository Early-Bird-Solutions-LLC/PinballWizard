using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Landing;
using PinballWizard.Application.Persistence;
using Xunit;

namespace PinballWizard.Scraper.Tests.Application.Landing;

public sealed class LandingServiceTests
{
    private readonly ISeedQuestionLoader _loader = Substitute.For<ISeedQuestionLoader>();
    private readonly IFeaturedMachineRepository _featuredRepo = Substitute.For<IFeaturedMachineRepository>();

    // Service without Cosmos (repo absent — degraded mode: FeaturedMachines = null).
    private readonly LandingService _serviceNoRepo;
    // Service with Cosmos repo wired.
    private readonly LandingService _serviceWithRepo;

    public LandingServiceTests()
    {
        _serviceNoRepo = new LandingService(
            _loader,
            NullLogger<LandingService>.Instance);

        _serviceWithRepo = new LandingService(
            _loader,
            NullLogger<LandingService>.Instance,
            _featuredRepo);
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

        var response = await _serviceNoRepo.GetLandingAsync(CancellationToken.None);

        Assert.Equal(2, response.SeedQuestions.Count);
        Assert.Equal("slug-rules", response.SeedQuestions[0].Slug);
        Assert.Equal("slug-valuation", response.SeedQuestions[1].Slug);
    }

    [Fact]
    public async Task GetLandingAsync_LoaderReturnedEmptyList_ResponseHasEmptySeedQuestions()
    {
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);

        var response = await _serviceNoRepo.GetLandingAsync(CancellationToken.None);

        Assert.Empty(response.SeedQuestions);
    }

    // ── FeaturedMachines — with repo wired (PR-L2 behavior) ──────────────────

    [Fact]
    public async Task GetLandingAsync_WithRepo_PopulatesFeaturedMachinesFromRepo()
    {
        // Behavioral assertion: when IFeaturedMachineRepository is present,
        // GetLandingAsync must call GetAllAsync and populate FeaturedMachines.
        // This is the load-bearing test for PR-L2.
        var machines = new List<FeaturedMachine>
        {
            new("stern-godzilla", "Godzilla Pro", null, 1, "King of the monsters"),
            new("jjp-wonka", "Wonka", null, 2, "Pure imagination"),
        };

        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);
        _featuredRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FeaturedMachine>)machines.AsReadOnly());

        var response = await _serviceWithRepo.GetLandingAsync(CancellationToken.None);

        Assert.NotNull(response.FeaturedMachines);
        Assert.Equal(2, response.FeaturedMachines!.Count);
        Assert.Equal("stern-godzilla", response.FeaturedMachines[0].MachineId);
        Assert.Equal("Godzilla Pro", response.FeaturedMachines[0].Title);
        Assert.Equal(1, response.FeaturedMachines[0].DisplayOrder);
    }

    [Fact]
    public async Task GetLandingAsync_WithRepo_RepoReturnsEmptyList_FeaturedMachinesIsNull()
    {
        // Empty container is treated as "not yet seeded" — degrade to null
        // rather than returning an empty list, so the landing page renders
        // the "coming soon" fallback instead of an empty strip.
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);
        _featuredRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FeaturedMachine>)[]);

        var response = await _serviceWithRepo.GetLandingAsync(CancellationToken.None);

        Assert.Null(response.FeaturedMachines);
    }

    // ── FeaturedMachines — without repo wired (degraded mode) ────────────────

    [Fact]
    public async Task GetLandingAsync_WithoutRepo_FeaturedMachinesIsNull()
    {
        // When Cosmos is not configured, FeaturedMachines degrades gracefully
        // to null — matches the PR-L1 placeholder contract and prevents the
        // API from failing to start in local dev without an emulator.
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);

        var response = await _serviceNoRepo.GetLandingAsync(CancellationToken.None);

        Assert.Null(response.FeaturedMachines);
    }

    [Fact]
    public async Task GetLandingAsync_WithoutRepo_DoesNotCallFeaturedRepo()
    {
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);

        // serviceNoRepo was constructed WITHOUT _featuredRepo — the substitute
        // should never be called.
        await _serviceNoRepo.GetLandingAsync(CancellationToken.None);

        await _featuredRepo.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    // ── SystemStatus is still null (PR-L3 fills this) ────────────────────────

    [Fact]
    public async Task GetLandingAsync_SystemStatusIsNull()
    {
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);

        var response = await _serviceNoRepo.GetLandingAsync(CancellationToken.None);

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

        await _serviceNoRepo.GetLandingAsync(cts.Token);

        await _loader.Received(1).LoadAsync(cts.Token);
    }

    [Fact]
    public async Task GetLandingAsync_WithRepo_PassesCancellationTokenToRepo()
    {
        using var cts = new CancellationTokenSource();
        _loader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedQuestion>)[]);
        _featuredRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FeaturedMachine>)[]);

        await _serviceWithRepo.GetLandingAsync(cts.Token);

        await _featuredRepo.Received(1).GetAllAsync(cts.Token);
    }
}
