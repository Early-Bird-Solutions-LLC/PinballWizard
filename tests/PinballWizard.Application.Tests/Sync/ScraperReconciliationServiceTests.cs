using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Sync;

/// <summary>
/// Tests for <see cref="ScraperReconciliationService"/>. Mocks
/// <see cref="IMachineRepository"/> via NSubstitute and asserts the
/// behaviours ADR 0011 contracts: slug-fast-path match,
/// title-normalize bootstrap fallback, ambiguous-title rejection,
/// unmatched-record skip, scraper-owned field merge, and idempotency.
/// </summary>
public sealed class ScraperReconciliationServiceTests
{
    private readonly IMachineRepository _repo = Substitute.For<IMachineRepository>();
    private readonly TimeProvider _clock = new FakeClock(new DateTimeOffset(2026, 5, 2, 18, 0, 0, TimeSpan.Zero));
    private readonly ScraperReconciliationService _service;

    public ScraperReconciliationServiceTests()
    {
        _service = new ScraperReconciliationService(
            _repo,
            _clock,
            NullLogger<ScraperReconciliationService>.Instance);
    }

    // ── Match by slug fast path ──────────────────────────────────────────

    [Fact]
    public async Task SlugMatch_MergesEditionsAndUpserts()
    {
        var existing = MakeMachine("GRBN-MQR4P", "stern", "Stranger Things");
        existing.ManufacturerSlugs["stern"] = "stranger-things";
        StubPartition("stern", existing);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_stranger-things",
            Title = "Stranger Things",
            Slug = "stranger-things",
            GamePageUrl = "https://sternpinball.com/game/stranger-things/",
            Editions =
            {
                new EditionInfo { Name = "Pro", Msrp = "$6,999", Availability = "Available" },
                new EditionInfo { Name = "Premium", Msrp = "$8,999" },
            },
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.MatchedBySlug);
        Assert.Equal(0, result.MatchedByTitle);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(1, result.Upserts);

        Assert.Equal(2, existing.Editions.Count);
        Assert.Contains(existing.Editions, e => e.Name == "Pro" && e.Msrp == "$6,999");
        Assert.Equal(_clock.GetUtcNow(), existing.LastSeenAt);
        await _repo.Received(1).UpsertAsync(existing, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlugMatch_PreservesOpdbOwnedFields()
    {
        var existing = MakeMachine("GRBN-MQR4P", "stern", "Stranger Things", manufacturerDisplayName: "Stern Pinball");
        existing.Year = 2019;
        existing.Designers = ["Brian Eddy"];
        existing.Themes = ["TV", "Horror"];
        existing.ManufacturerSlugs["stern"] = "stranger-things";
        StubPartition("stern", existing);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_stranger-things",
            Title = "Different Scraper Title",
            Slug = "stranger-things",
            GamePageUrl = "https://sternpinball.com/game/stranger-things/",
        });

        await _service.ReconcileAsync(catalog, CancellationToken.None);

        // Title / Year / Designers / Themes are OPDB-owned per ADR 0011.
        Assert.Equal("Stranger Things", existing.Title);
        Assert.Equal(2019, existing.Year);
        Assert.Equal(["Brian Eddy"], existing.Designers);
        Assert.Equal(["TV", "Horror"], existing.Themes);
        Assert.Equal("Stern Pinball", existing.ManufacturerDisplayName);
    }

    // ── Bootstrap title-normalize fallback ───────────────────────────────

    [Fact]
    public async Task BootstrapTitleMatch_PopulatesSlugMapAndUpserts()
    {
        // Empty ManufacturerSlugs — simulates the first reconcile after
        // OPDB sync has run but before any scraper has reconciled.
        var existing = MakeMachine("GBL66-MJ8RP", "jjp", "The Godfather");
        Assert.Empty(existing.ManufacturerSlugs);
        StubPartition("jjp", existing);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_jjp_the-godfather-pinball-game-collectors-edition",
            Title = "The Godfather",
            Slug = "the-godfather-pinball-game-collectors-edition",
            GamePageUrl = "https://jerseyjackpinball.com/products/the-godfather-pinball-game-collectors-edition",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(0, result.MatchedBySlug);
        Assert.Equal(1, result.MatchedByTitle);
        Assert.Equal(1, result.Upserts);

        Assert.Equal("the-godfather-pinball-game-collectors-edition",
            existing.ManufacturerSlugs["jjp"]);
    }

    [Fact]
    public async Task BootstrapTitleMatch_NormalizesPunctuationAndCase()
    {
        var existing = MakeMachine("OPDB-1", "stern", "James Bond 007");
        StubPartition("stern", existing);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_james-bond-007",
            Title = "james bond 007",  // case differs
            Slug = "james-bond-007",
            GamePageUrl = "https://sternpinball.com/game/james-bond-007/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.MatchedByTitle);
        Assert.Equal("james-bond-007", existing.ManufacturerSlugs["stern"]);
    }

    [Fact]
    public async Task AmbiguousTitle_LogsAndSkips()
    {
        // Two Machines in the same partition with the same normalized
        // title — the reconciler must NOT pick one arbitrarily.
        var first = MakeMachine("OPDB-A", "stern", "Star Trek");
        first.Year = 1979;
        var second = MakeMachine("OPDB-B", "stern", "Star Trek");
        second.Year = 2013;
        StubPartition("stern", first, second);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_star-trek",
            Title = "Star Trek",
            Slug = "star-trek",
            GamePageUrl = "https://sternpinball.com/game/star-trek/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(0, result.MatchedBySlug);
        Assert.Equal(0, result.MatchedByTitle);
        Assert.Equal(1, result.AmbiguousTitle);
        Assert.Equal(0, result.Upserts);
        await _repo.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    // ── Unmatched / failure paths ────────────────────────────────────────

    [Fact]
    public async Task UnmatchedScrapedGame_IsSkippedNotInserted()
    {
        // Empty partition — OPDB doesn't know this manufacturer's machines yet.
        StubPartition("spooky");

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_spooky_beetlejuice",
            Title = "Beetlejuice",
            Slug = "beetlejuice",
            GamePageUrl = "https://www.spookypinball.com/beetlejuice/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.Unmatched);
        Assert.Equal(0, result.Upserts);
        await _repo.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnrecognisedGameIdPrefix_IsCountedAsFailedMapping()
    {
        var catalog = CatalogOf(new GameRecord
        {
            GameId = "foreign_format_id",
            Title = "X",
            Slug = "x",
            GamePageUrl = "https://example.com/x",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.FailedMapping);
        Assert.Equal(0, result.Upserts);
    }

    // ── Idempotency / partition cache ────────────────────────────────────

    [Fact]
    public async Task SamePartitionStreamedOnceForBatchOfRecords()
    {
        var existing = MakeMachine("OPDB-A", "stern", "Stranger Things");
        existing.ManufacturerSlugs["stern"] = "stranger-things";
        var also = MakeMachine("OPDB-B", "stern", "Foo Fighters");
        also.ManufacturerSlugs["stern"] = "foo-fighters";
        StubPartition("stern", existing, also);

        var catalog = new GameCatalog
        {
            Games =
            {
                new GameRecord { GameId = "game_stranger-things", Title = "X", Slug = "stranger-things", GamePageUrl = "https://x" },
                new GameRecord { GameId = "game_foo-fighters", Title = "Y", Slug = "foo-fighters", GamePageUrl = "https://y" },
            },
        };

        await _service.ReconcileAsync(catalog, CancellationToken.None);

        // Only one stream call per partition (cache hits the second time).
        _repo.Received(1).StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReReconcileWithSameData_StillMatchesViaSlugFastPath()
    {
        // First run populates the slug map; second run should hit the slug
        // fast path (not the title fallback) on the same data.
        var existing = MakeMachine("OPDB-A", "spooky", "Beetlejuice");
        StubPartition("spooky", existing);

        var record = new GameRecord
        {
            GameId = "game_spooky_beetlejuice",
            Title = "Beetlejuice",
            Slug = "beetlejuice",
            GamePageUrl = "https://www.spookypinball.com/beetlejuice/",
        };

        var first = await _service.ReconcileAsync(CatalogOf(record), CancellationToken.None);
        Assert.Equal(1, first.MatchedByTitle);

        // Manually re-stub so the same Machine (now with slug populated)
        // is what the second run sees.
        StubPartition("spooky", existing);
        var second = await _service.ReconcileAsync(CatalogOf(record), CancellationToken.None);

        Assert.Equal(1, second.MatchedBySlug);
        Assert.Equal(0, second.MatchedByTitle);
    }

    // ── Group-aware multi-match (edition families) ───────────────────────

    [Fact]
    public async Task SameGroupTitleCollision_WritesSlugToAllBasesInGroup()
    {
        // Two Stern Godzilla base machines — an edition family: same manufacturer,
        // same OPDB group segment "GweeP", same release year 2021, franchise title
        // "Godzilla". Pro (GweeP-MW95j) + Premium/LE (GweeP-Ml9pZ). One scraped
        // bare-franchise "Godzilla" page → slug written to BOTH, NOT dropped.
        var pro = MakeMachine("GweeP-MW95j", "stern", "Godzilla (Pro)");
        pro.GroupId = "GweeP"; pro.Year = 2021;
        var premLe = MakeMachine("GweeP-Ml9pZ", "stern", "Godzilla (Premium/LE)");
        premLe.GroupId = "GweeP"; premLe.Year = 2021;
        StubPartition("stern", pro, premLe);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_godzilla",
            Title = "Godzilla",
            Slug = "godzilla",
            GamePageUrl = "https://sternpinball.com/game/godzilla/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.MatchedByGroup);
        Assert.Equal(0, result.AmbiguousTitle);
        Assert.Equal(2, result.Upserts);
        Assert.Equal("godzilla", pro.ManufacturerSlugs["stern"]);
        Assert.Equal("godzilla", premLe.ManufacturerSlugs["stern"]);
    }

    [Fact]
    public async Task SameFranchiseDifferentYear_StaysAmbiguous_NotMergedAsEdition()
    {
        // Same franchise title + same manufacturer partition but DIFFERENT
        // release years — a reissue/remake, NOT an edition family (the Big Ben
        // 1954-vs-1975 pattern). Must NOT smear one slug across both → ambiguous.
        // (Uses the Stern partition so the GameId prefix maps to a real key.)
        var older = MakeMachine("G5QBX-MQd1L", "stern", "Big Ben");
        older.GroupId = "G5QBX"; older.Year = 1954;
        var newer = MakeMachine("GRBo3-MLv0z", "stern", "Big Ben");
        newer.GroupId = "GRBo3"; newer.Year = 1975;
        StubPartition("stern", older, newer);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_big-ben",
            Title = "Big Ben",
            Slug = "big-ben",
            GamePageUrl = "https://sternpinball.com/game/big-ben/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.AmbiguousTitle);
        Assert.Equal(0, result.MatchedByGroup);
        Assert.Equal(0, result.Upserts);
        Assert.Empty(older.ManufacturerSlugs);
        Assert.Empty(newer.ManufacturerSlugs);
    }

    [Fact]
    public async Task SameYearDifferentGroupSegment_StaysAmbiguous()
    {
        // Same franchise + same year but DIFFERENT OPDB group segments → not a
        // single edition family (the segment must also agree) → ambiguous.
        var a = MakeMachine("AAAA-1", "stern", "Mystery");
        a.GroupId = "AAAA"; a.Year = 2020;
        var b = MakeMachine("BBBB-1", "stern", "Mystery");
        b.GroupId = "BBBB"; b.Year = 2020;
        StubPartition("stern", a, b);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_mystery", Title = "Mystery", Slug = "mystery",
            GamePageUrl = "https://sternpinball.com/game/mystery/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.AmbiguousTitle);
        Assert.Equal(0, result.MatchedByGroup);
        Assert.Equal(0, result.Upserts);
    }

    [Fact]
    public async Task SameGroupDifferentYears_WritesSlugToAllBasesInGroup()
    {
        // CGC Medieval Madness pattern (issue #655 Gap 1): 6 editions share
        // GroupId "G5pe4" but span multiple OPDB manufacture years (Remake 2015,
        // Cosmic Edition 2021, etc.). The old year guard in isEditionFamily
        // classified this as Ambiguous → no slug written. The fix: same GroupId
        // is sufficient — year is NOT required for franchise-slug-stamping because
        // the scraper's slug covers the whole franchise regardless of release year.
        // The genuine "different franchise, same title" case (Big Ben 1954 vs 1975)
        // always has DIFFERENT GroupIds and stays Ambiguous correctly.
        var remake = MakeMachine("G5pe4-MePZv", "cgc", "Medieval Madness");
        remake.GroupId = "G5pe4"; remake.Year = 2015;
        var cosmic = MakeMachine("G5pe4-MkPRV", "cgc", "Medieval Madness");
        cosmic.GroupId = "G5pe4"; cosmic.Year = 2021;
        StubPartition("cgc", remake, cosmic);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_cgc_medieval-madness",
            Title = "Medieval Madness",
            Slug = "medieval-madness",
            GamePageUrl = "https://www.chicago-gaming.com/coinop/medieval-madness/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.MatchedByGroup);
        Assert.Equal(0, result.AmbiguousTitle);
        Assert.Equal(2, result.Upserts);
        Assert.Equal("medieval-madness", remake.ManufacturerSlugs["cgc"]);
        Assert.Equal("medieval-madness", cosmic.ManufacturerSlugs["cgc"]);
    }

    [Fact]
    public async Task SameGroupNullYear_WritesSlugToAllBasesInGroup()
    {
        // Same GroupId + null Year: OPDB sometimes lacks manufacture-date data
        // for newer machines. A null year must NOT block slug-stamping when
        // GroupId already identifies the franchise unambiguously.
        var a = MakeMachine("G5pe4-MePZv", "cgc", "Medieval Madness");
        a.GroupId = "G5pe4"; a.Year = null;
        var b = MakeMachine("G5pe4-MkPRV", "cgc", "Medieval Madness");
        b.GroupId = "G5pe4"; b.Year = null;
        StubPartition("cgc", a, b);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_cgc_medieval-madness",
            Title = "Medieval Madness",
            Slug = "medieval-madness",
            GamePageUrl = "https://www.chicago-gaming.com/coinop/medieval-madness/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.MatchedByGroup);
        Assert.Equal(0, result.AmbiguousTitle);
        Assert.Equal(2, result.Upserts);
        Assert.Equal("medieval-madness", a.ManufacturerSlugs["cgc"]);
        Assert.Equal("medieval-madness", b.ManufacturerSlugs["cgc"]);
    }

    // ── Decoration-stripped title match ──────────────────────────────────

    [Fact]
    public async Task DecoratedScrapedTitle_MatchesUndecoratedCatalogTitle()
    {
        // CGC scrapes "Cactus Canyon Remake"; OPDB catalog title is "Cactus Canyon".
        var existing = MakeMachine("OPDB-CC", "cgc", "Cactus Canyon");
        StubPartition("cgc", existing);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_cgc_cactus-canyon",
            Title = "Cactus Canyon Remake",
            Slug = "cactus-canyon",
            GamePageUrl = "https://chicago-gaming.com/coinop/cactus-canyon/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.MatchedByTitle);
        Assert.Equal("cactus-canyon", existing.ManufacturerSlugs["cgc"]);
    }

    // ── Trailing-qualifier fallback (issue #660) ─────────────────────────
    //
    // CGC's current listing for Medieval Madness is "Medieval Madness Merlin Edition
    // Pinball". NormalizeFranchiseTitle strips the trailing "pinball" decoration but
    // leaves "merlinedition", so the exact franchise match (Pass 2) finds nothing.
    // Pass 3 checks whether the scraped franchise starts with a catalog franchise title
    // AND the remainder is entirely composed of DecorationWords tokens.

    [Fact]
    public async Task TrailingQualifierFallback_MerlinEditionPinball_MatchesEditionFamily()
    {
        // Positive case — the exact title from issue #660. Two CGC "Medieval Madness"
        // bases share GroupId "G5pe4" (Remake 2015, Cosmic Edition 2021). The scraped
        // title "Medieval Madness Merlin Edition Pinball" must match the whole edition
        // family, not just one member, and must write the slug to both.
        //
        // NormalizeFranchiseTitle("Medieval Madness Merlin Edition Pinball"):
        //   single-strips "pinball" -> "medievalmadnessmerlinedition"
        // Exact match vs catalog "medievalmadness": FAILS (Pass 2 returns 0).
        // Fallback: starts-with "medievalmadness" = true; remainder "merlinedition" is
        // a DecorationWords token -> fully consumed -> MATCH (Pass 3). Two same-GroupId
        // machines -> MatchedByGroup.
        var remake = MakeMachine("G5pe4-MePZv", "cgc", "Medieval Madness");
        remake.GroupId = "G5pe4"; remake.Year = 2015;
        var cosmic = MakeMachine("G5pe4-MkPRV", "cgc", "Medieval Madness");
        cosmic.GroupId = "G5pe4"; cosmic.Year = 2021;
        StubPartition("cgc", remake, cosmic);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_cgc_medieval-madness",
            Title = "Medieval Madness Merlin Edition Pinball",
            Slug = "medieval-madness",
            GamePageUrl = "https://www.chicago-gaming.com/coinop/medieval-madness/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.MatchedByGroup);
        Assert.Equal(0, result.AmbiguousTitle);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(2, result.Upserts);
        Assert.Equal("medieval-madness", remake.ManufacturerSlugs["cgc"]);
        Assert.Equal("medieval-madness", cosmic.ManufacturerSlugs["cgc"]);
    }

    [Fact]
    public async Task TrailingQualifierFallback_NonQualifierRemainder_DoesNotFalseMatch()
    {
        // False-positive guard — the most critical property of the fallback.
        // "Medieval Madness Returns" shares the prefix "medievalmadness" with the
        // catalog game, but "returns" is not in the qualifier set. The fallback must
        // NOT match — accepting it would silently map a different game (or a future
        // different franchise) onto the Medieval Madness slug. Result: Unmatched.
        //
        // This test proves the fallback is a narrow qualifier-only check, not a loose
        // starts-with/prefix match. Any word absent from DecorationWords blocks the match.
        var machine = MakeMachine("G5pe4-MePZv", "cgc", "Medieval Madness");
        machine.GroupId = "G5pe4";
        StubPartition("cgc", machine);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_cgc_medieval-madness-returns",
            Title = "Medieval Madness Returns",   // "returns" is not a qualifier
            Slug = "medieval-madness-returns",
            GamePageUrl = "https://www.chicago-gaming.com/coinop/medieval-madness-returns/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.Unmatched);
        Assert.Equal(0, result.Upserts);
        Assert.Empty(machine.ManufacturerSlugs);
    }

    [Fact]
    public async Task TrailingQualifierFallback_NonFamilyMultiMatch_ReturnsAmbiguous()
    {
        // Ambiguity guard. Two catalog machines share the same franchise title "Foo"
        // but belong to different OPDB groups (cross-era title reuse, the "Big Ben"
        // pattern). The scraped title "Foo Edition Pinball" fails the exact match
        // ("fooedition" != "foo") and then hits the fallback:
        //   NormalizeFranchiseTitle strips "pinball" -> "fooedition"
        //   Starts with "foo" (both machines) AND remainder "edition" is a qualifier.
        //
        // Both machines are prefix-matched but they are NOT an edition family
        // (different GroupIds), so the reconciler must return Ambiguous — the same
        // posture as the exact-match ambiguity guard (#657). The slug must not be
        // smeared onto either machine.
        var older = MakeMachine("AAAA-1", "cgc", "Foo");
        older.GroupId = "AAAA";
        var newer = MakeMachine("BBBB-1", "cgc", "Foo");
        newer.GroupId = "BBBB";
        StubPartition("cgc", older, newer);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_cgc_foo",
            Title = "Foo Edition Pinball",
            Slug = "foo",
            GamePageUrl = "https://www.chicago-gaming.com/coinop/foo/",
        });

        var result = await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(1, result.AmbiguousTitle);
        Assert.Equal(0, result.Upserts);
        Assert.Empty(older.ManufacturerSlugs);
        Assert.Empty(newer.ManufacturerSlugs);
    }

    // ── NormalizeTitle pure function ─────────────────────────────────────

    [Theory]
    [InlineData("Stranger Things", "strangerthings")]
    [InlineData("STRANGER THINGS", "strangerthings")]
    [InlineData("Stranger Things (Pro)", "strangerthingspro")]
    [InlineData("James Bond 007", "jamesbond007")]
    [InlineData("AC/DC", "acdc")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void NormalizeTitle_StripsNonAlphanumericAndLowercases(string? input, string expected)
    {
        Assert.Equal(expected, ScraperReconciliationService.NormalizeTitle(input));
    }

    // ── NormalizeFranchiseTitle pure function ────────────────────────────

    [Theory]
    [InlineData("Godzilla (Pro)", "godzilla")]
    [InlineData("Godzilla (Premium/LE)", "godzilla")]
    [InlineData("Godzilla", "godzilla")]
    [InlineData("The Rolling Stones (LE)", "therollingstones")]
    [InlineData("Stranger Things", "strangerthings")]
    [InlineData(null, "")]
    public void NormalizeFranchiseTitle_StripsTrailingEditionParenthetical(string? input, string expected)
    {
        Assert.Equal(expected, ScraperReconciliationService.NormalizeFranchiseTitle(input));
    }

    // ── Game-page content fields (overview / trailer / accessories) ─────

    [Fact]
    public async Task ReconcileAsync_CopiesOverviewTrailerAndAccessories_OntoMatchedMachine()
    {
        var existing = MakeMachine("GweeP-MW95j", "stern", "Godzilla");
        existing.ManufacturerSlugs["stern"] = "godzilla";
        StubPartition("stern", existing);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_godzilla",
            Title = "Godzilla",
            Slug = "godzilla",
            GamePageUrl = "https://sternpinball.com/game/godzilla/",
            OverviewProse = "Battle Godzilla across the city.",
            TrailerUrl = "https://www.youtube.com/watch?v=abc123",
            Accessories =
            {
                new AccessoryInfo
                {
                    Name = "Topper",
                    Price = "$1,299.99",
                    ProductUrl = "https://shop.sternpinball.com/products/godzilla-topper",
                },
            },
        });

        await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal("Battle Godzilla across the city.", existing.OverviewProse);
        Assert.Equal("https://sternpinball.com/game/godzilla/", existing.OverviewSourceUrl);
        Assert.Equal("https://www.youtube.com/watch?v=abc123", existing.TrailerUrl);
        Assert.Equal("Topper", existing.Accessories.Single().Name);
        await _repo.Received(1).UpsertAsync(existing, Arg.Any<CancellationToken>());
    }

    // ── Year enrichment from scraper ────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_CopiesReleaseYear_WhenMachineYearIsNull()
    {
        // Arrange — machine has no OPDB year; scraper provides one
        var machine = MakeMachine("GweeP-MW95j", "stern", "Godzilla");
        machine.Year = null;
        machine.ManufacturerSlugs["stern"] = "godzilla";
        StubPartition("stern", machine);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_godzilla",
            Title = "Godzilla",
            Slug = "godzilla",
            GamePageUrl = "https://sternpinball.com/game/godzilla/",
            ReleaseYear = 2023,
        });

        // Act
        await _service.ReconcileAsync(catalog, CancellationToken.None);

        // Assert
        Assert.Equal(2023, machine.Year);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotOverwriteExistingYear_WhenMachineYearIsSet()
    {
        // Existing OPDB year must not be stomped by the scraper value.
        var machine = MakeMachine("GweeP-MW95j", "stern", "Godzilla");
        machine.Year = 2021;
        machine.ManufacturerSlugs["stern"] = "godzilla";
        StubPartition("stern", machine);

        var catalog = CatalogOf(new GameRecord
        {
            GameId = "game_godzilla",
            Title = "Godzilla",
            Slug = "godzilla",
            GamePageUrl = "https://sternpinball.com/game/godzilla/",
            ReleaseYear = 2023,
        });

        await _service.ReconcileAsync(catalog, CancellationToken.None);

        Assert.Equal(2021, machine.Year);
    }

    // ── Cross-reference slug backfill (issue #672) ───────────────────────
    //
    // Root cause: Machine.ManufacturerSlugs is populated only by ReconcileAsync,
    // driven by a scraper run's own GameCatalog. Stern titles retired from the
    // currently-marketed lineup (Iron Man, Spider-Man, AC/DC, etc.) never appear
    // in a fresh GameCatalog, so their slug stays empty forever — even though
    // scraped_documents_raw already carries a cross-reference to their game page
    // (captured when a manual's "Specs & Manual tab" was scraped). This backfill
    // recovers the slug from that already-stored provenance instead of re-scraping.

    [Fact]
    public async Task BackfillSlugs_CrossReferenceMatchesOneMachine_SetsSlugAndUpserts()
    {
        var machine = MakeMachine("G43W4-MKNW0", "stern", "AC/DC");
        StubPartition("stern", machine);

        var raw = MakeRaw(crossRefs:
        [
            new CrossReference
            {
                AlsoFoundAt = "https://sternpinball.com/game/ac-dc/",
                DiscoveryContext = "Game Page → Specs & Manual tab",
                DiscoveredAt = DateTime.UtcNow,
            },
        ]);

        var result = await _service.BackfillSlugsFromCrossReferencesAsync(ToAsyncRaw(raw), CancellationToken.None);

        Assert.Equal(1, result.MatchedSingle);
        Assert.Equal(0, result.Ambiguous);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(1, result.Upserts);
        Assert.Equal("ac-dc", machine.ManufacturerSlugs["stern"]);
        await _repo.Received(1).UpsertAsync(machine, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackfillSlugs_SlugAlreadyPresentOnSomeMachine_IsNoOp()
    {
        var machine = MakeMachine("G43W4-MKNW0", "stern", "AC/DC");
        machine.ManufacturerSlugs["stern"] = "ac-dc";
        StubPartition("stern", machine);

        var raw = MakeRaw(crossRefs:
        [
            new CrossReference
            {
                AlsoFoundAt = "https://sternpinball.com/game/ac-dc/",
                DiscoveryContext = "Game Page → Specs & Manual tab",
                DiscoveredAt = DateTime.UtcNow,
            },
        ]);

        var result = await _service.BackfillSlugsFromCrossReferencesAsync(ToAsyncRaw(raw), CancellationToken.None);

        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(0, result.Upserts);
        await _repo.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackfillSlugs_EditionFamilyMultiMatch_SetsSlugOnAllBases()
    {
        // Two Stern Iron Man base machines sharing one GroupId — an edition
        // family (mirrors SameGroupTitleCollision_WritesSlugToAllBasesInGroup).
        var pro = MakeMachine("GRVq4-MLyxq", "stern", "Iron Man");
        pro.GroupId = "GRVq4";
        var vault = MakeMachine("GRVq4-MDRKr", "stern", "Iron Man");
        vault.GroupId = "GRVq4";
        StubPartition("stern", pro, vault);

        var raw = MakeRaw(crossRefs:
        [
            new CrossReference
            {
                AlsoFoundAt = "https://sternpinball.com/game/iron-man/",
                DiscoveryContext = "Game Page → Specs & Manual tab",
                DiscoveredAt = DateTime.UtcNow,
            },
        ]);

        var result = await _service.BackfillSlugsFromCrossReferencesAsync(ToAsyncRaw(raw), CancellationToken.None);

        Assert.Equal(1, result.MatchedGroup);
        Assert.Equal(0, result.Ambiguous);
        Assert.Equal(2, result.Upserts);
        Assert.Equal("iron-man", pro.ManufacturerSlugs["stern"]);
        Assert.Equal("iron-man", vault.ManufacturerSlugs["stern"]);
    }

    [Fact]
    public async Task BackfillSlugs_AmbiguousNonFamilyMultiMatch_IsSkipped()
    {
        // Same title, DIFFERENT GroupId — a genuine cross-year/cross-game title
        // collision (the Big Ben 1954-vs-1975 pattern), not an edition family.
        var older = MakeMachine("OPDB-A", "stern", "Mystery");
        older.GroupId = "AAAA";
        var newer = MakeMachine("OPDB-B", "stern", "Mystery");
        newer.GroupId = "BBBB";
        StubPartition("stern", older, newer);

        var raw = MakeRaw(crossRefs:
        [
            new CrossReference
            {
                AlsoFoundAt = "https://sternpinball.com/game/mystery/",
                DiscoveryContext = "Game Page → Specs & Manual tab",
                DiscoveredAt = DateTime.UtcNow,
            },
        ]);

        var result = await _service.BackfillSlugsFromCrossReferencesAsync(ToAsyncRaw(raw), CancellationToken.None);

        Assert.Equal(1, result.Ambiguous);
        Assert.Equal(0, result.Upserts);
        Assert.Empty(older.ManufacturerSlugs);
        Assert.Empty(newer.ManufacturerSlugs);
    }

    [Fact]
    public async Task BackfillSlugs_NoMachineMatchesTitle_IsCountedUnmatched()
    {
        var machine = MakeMachine("OPDB-X", "stern", "Some Other Game");
        StubPartition("stern", machine);

        var raw = MakeRaw(crossRefs:
        [
            new CrossReference
            {
                AlsoFoundAt = "https://sternpinball.com/game/totally-different-title/",
                DiscoveryContext = "Game Page → Specs & Manual tab",
                DiscoveredAt = DateTime.UtcNow,
            },
        ]);

        var result = await _service.BackfillSlugsFromCrossReferencesAsync(ToAsyncRaw(raw), CancellationToken.None);

        Assert.Equal(1, result.Unmatched);
        Assert.Equal(0, result.Upserts);
    }

    [Fact]
    public async Task BackfillSlugs_UnrecognisedSourceType_ContributesNoCandidates()
    {
        // SourceType.ActionType default (Unknown mapping) → InferManufacturerKey
        // returns null, so this document contributes zero backfill candidates
        // rather than guessing a manufacturer.
        var raw = MakeRaw(sourceType: (SourceType)(-1), crossRefs:
        [
            new CrossReference
            {
                AlsoFoundAt = "https://example.com/game/whatever/",
                DiscoveryContext = "Unknown",
                DiscoveredAt = DateTime.UtcNow,
            },
        ]);

        var result = await _service.BackfillSlugsFromCrossReferencesAsync(ToAsyncRaw(raw), CancellationToken.None);

        Assert.Equal(0, result.CandidatesConsidered);
        Assert.Equal(0, result.Upserts);
    }

    [Fact]
    public async Task BackfillSlugs_SameSlugFromMultipleDocs_IsDeduplicated()
    {
        var machine = MakeMachine("G43W4-MKNW0", "stern", "AC/DC");
        StubPartition("stern", machine);

        var xref = new CrossReference
        {
            AlsoFoundAt = "https://sternpinball.com/game/ac-dc/",
            DiscoveryContext = "Game Page → Specs & Manual tab",
            DiscoveredAt = DateTime.UtcNow,
        };
        var docA = MakeRaw(documentId: "doc_a", crossRefs: [xref]);
        var docB = MakeRaw(documentId: "doc_b", crossRefs: [xref]);

        var result = await _service.BackfillSlugsFromCrossReferencesAsync(ToAsyncRaw(docA, docB), CancellationToken.None);

        Assert.Equal(1, result.CandidatesConsidered);
        Assert.Equal(1, result.Upserts);
        await _repo.Received(1).UpsertAsync(machine, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackfillSlugsFromCrossReferencesAsync_NullArgument_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.BackfillSlugsFromCrossReferencesAsync(null!, CancellationToken.None));
    }

    // ── Constructor null-checks ──────────────────────────────────────────

    [Fact]
    public void Constructor_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new ScraperReconciliationService(
            null!, _clock, NullLogger<ScraperReconciliationService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ScraperReconciliationService(
            _repo, null!, NullLogger<ScraperReconciliationService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ScraperReconciliationService(
            _repo, _clock, null!));
    }

    [Fact]
    public async Task ReconcileAsync_NullCatalog_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.ReconcileAsync(null!, CancellationToken.None));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void StubPartition(string manufacturer, params Machine[] machines)
    {
        _repo.StreamByManufacturerAsync(manufacturer, Arg.Any<CancellationToken>())
            .Returns(ToAsync(machines));
    }

    private static async IAsyncEnumerable<Machine> ToAsync(IEnumerable<Machine> machines)
    {
        foreach (var m in machines)
        {
            yield return m;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<RawDocumentRecord> ToAsyncRaw(params RawDocumentRecord[] docs)
    {
        foreach (var d in docs)
        {
            yield return d;
            await Task.Yield();
        }
    }

    private static RawDocumentRecord MakeRaw(
        string documentId = "doc_aabbccddeeff0011",
        string fileUrl = "https://sternpinball.com/wp-content/uploads/manual.pdf",
        SourceType sourceType = SourceType.ManualsPage,
        List<CrossReference>? crossRefs = null)
        => new()
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = DocumentType.Manual,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://sternpinball.com/manuals/",
                DiscoveryContext = "Manuals Page",
                FileUrl = fileUrl,
                ScrapedAt = DateTime.UtcNow,
                SourceType = sourceType,
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = DateTime.UtcNow,
            },
            CrossReferences = crossRefs ?? [],
        };

    private static Machine MakeMachine(
        string id, string manufacturer, string title, string? manufacturerDisplayName = null) => new()
    {
        Id = id,
        PartitionKey = manufacturer,
        ManufacturerDisplayName = manufacturerDisplayName ?? manufacturer,
        Title = title,
        FirstSeenAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LastSeenAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static GameCatalog CatalogOf(params GameRecord[] games) => new()
    {
        Games = [.. games],
    };

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
