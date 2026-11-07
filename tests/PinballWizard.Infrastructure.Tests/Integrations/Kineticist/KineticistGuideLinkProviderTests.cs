using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Infrastructure.Integrations.Kineticist;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Kineticist;

// Prefer OPDB-id lookup; slug fallback; fail closed on misses.
public sealed class KineticistGuideLinkProviderTests
{
    [Fact]
    public async Task GetGuideLinksAsync_PrefersOpdbIdLookup()
    {
        var api = Substitute.For<IKineticistApiClient>();
        api.GetGuideLinksAsync("G33YF", Arg.Any<CancellationToken>())
            .Returns(new KineticistGuideLinks(
                "godzilla-2021", "Godzilla", "G33YF",
                "https://www.kineticist.com/news/godzilla-pinball-tutorial",
                null, null));

        var provider = new KineticistGuideLinkProvider(api, NullLogger<KineticistGuideLinkProvider>.Instance);
        var result = await provider.GetGuideLinksAsync("G33YF", "godzilla-2021", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("godzilla-2021", result!.Slug);
        await api.Received(1).GetGuideLinksAsync("G33YF", Arg.Any<CancellationToken>());
        await api.DidNotReceive().GetGuideLinksAsync("godzilla-2021", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetGuideLinksAsync_FallsBackToSlugWhenOpdbMisses()
    {
        var api = Substitute.For<IKineticistApiClient>();
        api.GetGuideLinksAsync("G33YF", Arg.Any<CancellationToken>())
            .Returns((KineticistGuideLinks?)null);
        api.GetGuideLinksAsync("godzilla-2021", Arg.Any<CancellationToken>())
            .Returns(new KineticistGuideLinks(
                "godzilla-2021", "Godzilla", "G33YF",
                "https://www.kineticist.com/news/godzilla-pinball-tutorial",
                null, null));

        var provider = new KineticistGuideLinkProvider(api, NullLogger<KineticistGuideLinkProvider>.Instance);
        var result = await provider.GetGuideLinksAsync("G33YF", "godzilla-2021", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https://www.kineticist.com/news/godzilla-pinball-tutorial", result!.FullTutorialLink);
    }

    [Fact]
    public async Task GetGuideLinksAsync_BothMiss_ReturnsNull()
    {
        var api = Substitute.For<IKineticistApiClient>();
        api.GetGuideLinksAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((KineticistGuideLinks?)null);

        var provider = new KineticistGuideLinkProvider(api, NullLogger<KineticistGuideLinkProvider>.Instance);
        Assert.Null(await provider.GetGuideLinksAsync("missing", "also-missing", CancellationToken.None));
    }
}
