using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Infrastructure.Integrations.Kineticist;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Kineticist;

/// <summary>
/// Unit tests for <see cref="KineticistGameResolver"/>: exact-slug resolution,
/// the guarded title-search fallback for messy slugs, and the overlap guard
/// that prevents a weak search hit from producing a wrong (mis-grounded) link.
/// </summary>
public sealed class KineticistGameResolverTests
{
    private static KineticistGameResolver Build(FakeApi api) =>
        new(api, NullLogger<KineticistGameResolver>.Instance);

    [Fact]
    public async Task ResolveAsync_ExactSlugHit_ReturnsMatch_NoSearch()
    {
        var api = new FakeApi();
        api.Games["monster-bash"] = new KineticistGameMatch("monster-bash", "Monster Bash", ["Gr3EW-MD3Nj", "Gr3EW-M3dBn"]);

        var match = await Build(api).ResolveAsync("monster-bash", "Rock Monster: Learn to Play Monster Bash", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(["Gr3EW-MD3Nj", "Gr3EW-M3dBn"], match!.EditionOpdbIds);
        Assert.Equal(0, api.SearchCalls); // exact hit short-circuits the search
    }

    [Fact]
    public async Task ResolveAsync_SlugMiss_SearchHitWithOverlap_ResolvesViaSearch()
    {
        // "how-to-play-mata-hari-pinball" is not a Kineticist slug; the title
        // search on the cleaned query "mata hari" finds "Mata Hari".
        var api = new FakeApi();
        api.Games["mata-hari"] = new KineticistGameMatch("mata-hari", "Mata Hari", ["G417e-Mxxxx"]);
        api.SearchResults["mata hari"] = [new KineticistGameRef("Mata Hari", "mata-hari")];

        var match = await Build(api).ResolveAsync(
            "how-to-play-mata-hari-pinball", "Tempting Targets: Bally's Mata Hari", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(["G417e-Mxxxx"], match!.EditionOpdbIds);
        Assert.Equal(1, api.SearchCalls);
    }

    [Fact]
    public async Task ResolveAsync_SlugMiss_SearchHitWithoutOverlap_ReturnsNull()
    {
        // The search returns an off-topic top hit (no shared token with the
        // query) — the overlap guard rejects it rather than mis-linking.
        var api = new FakeApi();
        api.Games["sonic-the-hedgehog"] = new KineticistGameMatch("sonic-the-hedgehog", "Sonic", ["Gsnc-Mxxxx"]);
        api.SearchResults["obscure title"] = [new KineticistGameRef("Sonic the Hedgehog", "sonic-the-hedgehog")];

        var match = await Build(api).ResolveAsync(
            "obscure-title", "An Obscure Title Tutorial", CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task ResolveAsync_SlugMiss_NoSearchHits_ReturnsNull()
    {
        var api = new FakeApi(); // empty
        var match = await Build(api).ResolveAsync("nonexistent-game", "Nothing", CancellationToken.None);
        Assert.Null(match);
    }

    [Fact]
    public async Task ResolveAsync_AllNoiseSlug_ReturnsNull_WithoutSearching()
    {
        // A slug that reduces to nothing after noise-stripping yields an empty
        // search query — the resolver must short-circuit (never search with "").
        var api = new FakeApi();
        var match = await Build(api).ResolveAsync("the-pinball-rules-guide", "How to play", CancellationToken.None);
        Assert.Null(match);
        Assert.Equal(0, api.SearchCalls);
    }

    [Theory]
    [InlineData("how-to-play-mata-hari-pinball", "mata hari")]
    [InlineData("eight-ball-deluxe-rules-strategy", "eight ball deluxe")]
    [InlineData("monster-bash", "monster bash")]
    [InlineData("ac-dc", "ac dc")]
    [InlineData("the-getaway", "getaway")]
    [InlineData("the-pinball-rules-guide", "")]
    public void BuildSearchQuery_StripsNoiseTokens(string slug, string expected)
        => Assert.Equal(expected, KineticistGameResolver.BuildSearchQuery(slug));

    private sealed class FakeApi : IKineticistApiClient
    {
        public Dictionary<string, KineticistGameMatch> Games { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IReadOnlyList<KineticistGameRef>> SearchResults { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int SearchCalls { get; private set; }

        public Task<KineticistGameMatch?> GetGameBySlugAsync(string slug, CancellationToken cancellationToken)
            => Task.FromResult(Games.TryGetValue(slug, out var m) ? m : null);

        public Task<IReadOnlyList<KineticistGameRef>> SearchGamesAsync(string query, int limit, CancellationToken cancellationToken)
        {
            SearchCalls++;
            return Task.FromResult(SearchResults.TryGetValue(query, out var r) ? r : []);
        }
    }
}
