using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Findability;
using PinballWizard.Infrastructure.Findability;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Findability;

// Unit tests for MachineSuggestService (ADR-0049 phase 3).
//
// Coverage targets (per task brief):
//   - Edition collapse / dedup: multiple editions under one GroupId → one suggestion.
//   - Short-query guard: < 2 non-whitespace chars → empty, no index call.
//   - Unconfigured index (null): returns empty immediately.
//   - Rank order preserved after dedup.
//   - top cap respected (not more than `top` results returned).
//   - Ungrouped dedup: machines with no GroupId dedup by (Title, Manufacturer).
public sealed class MachineSuggestServiceTests
{
    private readonly IMachineSearchIndex _index = Substitute.For<IMachineSearchIndex>();

    private MachineSuggestService BuildService(IMachineSearchIndex? index = null)
        => new(index ?? _index, NullLogger<MachineSuggestService>.Instance);

    // ── Short-query guard ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]          // empty
    [InlineData(" ")]         // single space
    [InlineData("a")]         // one non-ws char
    [InlineData("  a  ")]     // one non-ws char, surrounded by whitespace
    public async Task SuggestAsync_QueryHasFewerThanTwoNonWsChars_ReturnsEmpty(string query)
    {
        var service = BuildService();

        var result = await service.SuggestAsync(query, top: 8, CancellationToken.None);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData("  a  ")]
    public async Task SuggestAsync_QueryHasFewerThanTwoNonWsChars_DoesNotCallIndex(string query)
    {
        var service = BuildService();

        await service.SuggestAsync(query, top: 8, CancellationToken.None);

        await _index.DidNotReceive().SearchAsync(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ab")]         // exactly 2 non-ws chars → triggers search
    [InlineData("  ab  ")]    // 2 non-ws chars with padding → triggers search
    [InlineData("godzilla")]  // normal query
    public async Task SuggestAsync_QueryHasAtLeastTwoNonWsChars_CallsIndex(string query)
    {
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>([]));
        var service = BuildService();

        await service.SuggestAsync(query, top: 8, CancellationToken.None);

        await _index.Received(1).SearchAsync(
            Arg.Is<string>(q => q == query),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ── Unconfigured index ────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_IndexIsNull_ReturnsEmpty()
    {
        var service = BuildService(index: null);

        var result = await service.SuggestAsync("godzilla", top: 8, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Edition collapse / dedup ──────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_MultipleHitsSameGroupId_CollapseToOne()
    {
        // Six editions of Medieval Madness sharing GroupId "GweeP" → one suggestion.
        var hits = BuildEditionHits("GweeP", new[]
        {
            ("GYWBZ-MkPrr", "Medieval Madness (Remake LE)",     "Chicago Gaming Company", 2016, 9.5),
            ("GYWBZ-MkPr2", "Medieval Madness (Remake Premium)","Chicago Gaming Company", 2016, 8.8),
            ("GYWBZ-MkPr3", "Medieval Madness (Remake)",        "Chicago Gaming Company", 2016, 8.0),
            ("GYWBZ-MkPr4", "Medieval Madness",                 "Williams",               1997, 7.5),
            ("GYWBZ-MkPr5", "Medieval Madness (Proto)",         "Williams",               1997, 6.0),
            ("GYWBZ-MkPr6", "Medieval Madness (Home Edition)",  "Williams",               1997, 5.0),
        });
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>(hits));
        var service = BuildService();

        var result = await service.SuggestAsync("medieval", top: 8, CancellationToken.None);

        var suggestion = Assert.Single(result);
        // The first (highest-scored) hit should be kept.
        Assert.Equal("GYWBZ-MkPrr", suggestion.OpdbId);
        Assert.Equal("Medieval Madness (Remake LE)", suggestion.Title);
    }

    [Fact]
    public async Task SuggestAsync_MultipleGroupIds_EachGroupProducesOneSuggestion()
    {
        // Two machines, each with multiple editions in different groups.
        var hits = new List<MachineSearchHit>
        {
            BuildHit("GW111-aaa1", "Godzilla Pro",     "Stern Pinball",          2021, "GW111", 10.0),
            BuildHit("GW111-aaa2", "Godzilla LE",      "Stern Pinball",          2021, "GW111",  9.0),
            BuildHit("GW222-bbb1", "Medieval Madness", "Chicago Gaming Company", 2016, "GW222",  8.5),
            BuildHit("GW222-bbb2", "Medieval Madness", "Williams",               1997, "GW222",  7.0),
        };
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>(hits));
        var service = BuildService();

        var result = await service.SuggestAsync("godzilla medieval", top: 8, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("GW111-aaa1", result[0].OpdbId); // top-scored Godzilla hit
        Assert.Equal("GW222-bbb1", result[1].OpdbId); // top-scored Medieval Madness hit
    }

    [Fact]
    public async Task SuggestAsync_HitsWithNoGroupId_DedupByTitleAndManufacturer()
    {
        // Two hits with the same title+manufacturer but null GroupId → collapse to one.
        var hits = new List<MachineSearchHit>
        {
            BuildHit("id1", "Attack from Mars", "Bally",  1995, groupId: null, score: 9.0),
            BuildHit("id2", "Attack from Mars", "Bally",  1995, groupId: null, score: 7.0),
        };
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>(hits));
        var service = BuildService();

        var result = await service.SuggestAsync("attack from mars", top: 8, CancellationToken.None);

        var suggestion = Assert.Single(result);
        Assert.Equal("id1", suggestion.OpdbId);
    }

    [Fact]
    public async Task SuggestAsync_DifferentTitlesSameManufacturerNoGroupId_EachSurfacesOnce()
    {
        // Different titles, same manufacturer, no group → two distinct suggestions.
        var hits = new List<MachineSearchHit>
        {
            BuildHit("id1", "Attack from Mars",  "Bally", 1995, groupId: null, score: 9.0),
            BuildHit("id2", "Creature from the Black Lagoon", "Bally", 1992, groupId: null, score: 8.0),
        };
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>(hits));
        var service = BuildService();

        var result = await service.SuggestAsync("from", top: 8, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    // ── Rank order preservation ───────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_RankOrderPreservedAfterCollapse()
    {
        // Index returns Godzilla (higher score) then Medieval Madness.
        // After dedup they should appear in the same order.
        var hits = new List<MachineSearchHit>
        {
            BuildHit("gz1", "Godzilla Pro",     "Stern Pinball",          2021, "GW1", 10.0),
            BuildHit("mm1", "Medieval Madness", "Chicago Gaming Company", 2016, "GW2",  8.0),
            BuildHit("gz2", "Godzilla LE",      "Stern Pinball",          2021, "GW1",  9.0), // same group as gz1, collapsed
        };
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>(hits));
        var service = BuildService();

        var result = await service.SuggestAsync("godzilla medieval", top: 8, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("gz1", result[0].OpdbId);  // Godzilla first (higher score)
        Assert.Equal("mm1", result[1].OpdbId);  // Medieval Madness second
    }

    // ── top cap ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_IndexReturnsManyDistinctHits_ResultCappedAtTop()
    {
        // Index returns 10 unique machines; top=3 → only 3 suggestions.
        var hits = Enumerable.Range(1, 10)
            .Select(i => BuildHit($"id{i}", $"Machine {i}", "Stern", 2020, groupId: null, score: 10 - i))
            .ToList();
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>(hits));
        var service = BuildService();

        var result = await service.SuggestAsync("machine", top: 3, CancellationToken.None);

        Assert.Equal(3, result.Count);
    }

    // ── MachineSuggestion field mapping ───────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_FieldsMappedCorrectlyFromHit()
    {
        var hits = new List<MachineSearchHit>
        {
            BuildHit("GYWBZ-MkPrr", "Willy Wonka & The Chocolate Factory", "Jersey Jack Pinball", 2019,
                groupId: "GYWBZ", score: 10.0),
        };
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>(hits));
        var service = BuildService();

        var result = await service.SuggestAsync("wonka", top: 8, CancellationToken.None);

        var suggestion = Assert.Single(result);
        Assert.Equal("GYWBZ-MkPrr", suggestion.OpdbId);
        Assert.Equal("Willy Wonka & The Chocolate Factory", suggestion.Title);
        Assert.Equal("Jersey Jack Pinball", suggestion.Manufacturer);
        Assert.Equal(2019, suggestion.Year);
    }

    [Fact]
    public async Task SuggestAsync_HitWithNullYear_SuggestionYearIsNull()
    {
        var hits = new List<MachineSearchHit>
        {
            BuildHit("id1", "Some Prototype", "Unknown Mfr", year: null, groupId: null, score: 5.0),
        };
        _index.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>(hits));
        var service = BuildService();

        var result = await service.SuggestAsync("prototype", top: 8, CancellationToken.None);

        var suggestion = Assert.Single(result);
        Assert.Null(suggestion.Year);
    }

    // ── CollapseEditions static (unit isolation) ──────────────────────────────

    [Fact]
    public void CollapseEditions_EmptyHits_ReturnsEmpty()
    {
        var result = MachineSuggestService.CollapseEditions([], top: 8);

        Assert.Empty(result);
    }

    [Fact]
    public void CollapseEditions_AllHitsSameGroupId_OneResult()
    {
        var hits = BuildEditionHits("GweeP", new[]
        {
            ("id1", "MM LE",      "CGC", 2016, 9.0),
            ("id2", "MM Premium", "CGC", 2016, 8.0),
            ("id3", "MM",         "CGC", 2016, 7.0),
        });

        var result = MachineSuggestService.CollapseEditions(hits, top: 8);

        var suggestion = Assert.Single(result);
        Assert.Equal("id1", suggestion.OpdbId);
    }

    [Fact]
    public void CollapseEditions_TopCap_HonorsLimit()
    {
        var hits = Enumerable.Range(1, 20)
            .Select(i => BuildHit($"id{i}", $"Machine {i}", "Stern", 2020, groupId: null, score: 20 - i))
            .ToList();

        var result = MachineSuggestService.CollapseEditions(hits, top: 5);

        Assert.Equal(5, result.Count);
    }

    // ── Overfetch multiplier ──────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_RequestsOverfetchedRawHitsFromIndex()
    {
        // For top=8, the service should request 8 * 4 = 32 raw hits (capped at 80).
        int? capturedRawTop = null;
        _index.SearchAsync(
                Arg.Any<string>(),
                Arg.Do<int>(t => capturedRawTop = t),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>([]));
        var service = BuildService();

        await service.SuggestAsync("godzilla", top: 8, CancellationToken.None);

        Assert.Equal(8 * MachineSuggestService.OverfetchMultiplier, capturedRawTop);
    }

    [Fact]
    public async Task SuggestAsync_OverfetchCapEnforced()
    {
        // For top=25, the uncapped overfetch would be 100, but MaxRawHits=80 clamps it.
        int? capturedRawTop = null;
        _index.SearchAsync(
                Arg.Any<string>(),
                Arg.Do<int>(t => capturedRawTop = t),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSearchHit>>([]));
        var service = BuildService();

        await service.SuggestAsync("godzilla", top: 25, CancellationToken.None);

        Assert.Equal(MachineSuggestService.MaxRawHits, capturedRawTop);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MachineSearchHit BuildHit(
        string opdbId,
        string title,
        string manufacturer,
        int? year,
        string? groupId,
        double score)
        => new(
            OpdbId: opdbId,
            Title: title,
            ManufacturerDisplayName: manufacturer,
            ManufacturerKey: manufacturer.ToLowerInvariant().Replace(" ", "-"),
            GroupId: groupId,
            Year: year,
            Score: score);

    private static List<MachineSearchHit> BuildEditionHits(
        string groupId,
        IEnumerable<(string OpdbId, string Title, string Manufacturer, int Year, double Score)> editions)
        => editions
            .Select(e => BuildHit(e.OpdbId, e.Title, e.Manufacturer, e.Year, groupId, e.Score))
            .ToList();
}
