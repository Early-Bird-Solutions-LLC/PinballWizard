using PinballWizard.Application;
using PinballWizard.Application.Downloading;
using PinballWizard.Core.Scraping;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Tests.Unit.Application;

/// <summary>
/// Pinned-down behavior for the post-DOM helpers in GamePageExtractors.
/// These cover the two regressions found in the 2026-05-02 catalog run:
/// the cookie banner winning the title selector race, and edition cards
/// appearing 3x because nested wrappers all match [class*="edition"].
/// </summary>
public sealed class GamePageExtractorsTests
{
    // ---------- SanitizeGameTitle ----------

    [Fact]
    public void SanitizeGameTitle_RejectsCookieBannerCandidate()
    {
        var candidates = new string?[]
        {
            "Your Privacy Choices / Cookie Settings",
            "Stranger Things",
        };

        var title = GamePageExtractors.SanitizeGameTitle(
            candidates, pageTitle: null, slug: "stranger-things");

        Assert.Equal("Stranger Things", title);
    }

    [Fact]
    public void SanitizeGameTitle_RejectsAllBannerVariants()
    {
        var banners = new[]
        {
            "Cookie Settings",
            "Privacy Notice",
            "Manage Preferences",
            "Skip to content",
            "your privacy choices",
        };

        foreach (var banner in banners)
        {
            var title = GamePageExtractors.SanitizeGameTitle(
                new string?[] { banner }, pageTitle: null, slug: "stranger-things");

            Assert.Equal("Stranger Things", title);
        }
    }

    [Fact]
    public void SanitizeGameTitle_RejectsSignupCtaH1()
    {
        // Stern templates a per-game H1 onto its newsletter signup widget,
        // e.g. "Sign up for Pokémon by Stern Pinball Updates!". This must
        // not win the candidate race against the real game H1 / page title.
        string?[] candidates =
        [
            "Sign up for Pokémon by Stern Pinball Updates!",
            "Pokémon",
        ];

        var title = GamePageExtractors.SanitizeGameTitle(
            candidates, pageTitle: null, slug: "pokemon");

        Assert.Equal("Pokémon", title);
    }

    [Fact]
    public void SanitizeGameTitle_RejectsCtaWhenItIsTheOnlyCandidate_FallsThroughToPageTitle()
    {
        string?[] candidates = ["Sign up for Pokémon by Stern Pinball Updates!"];
        var title = GamePageExtractors.SanitizeGameTitle(
            candidates,
            pageTitle: "Pokémon - Stern Pinball",
            slug: "pokemon");

        Assert.Equal("Pokémon", title);
    }

    [Fact]
    public void SanitizeGameTitle_StripsDashSuffixFromCandidate()
    {
        // Some Stern game pages have an H1 like "John Wick - Stern Pinball"
        // (templated from the page title). Strip the suffix wherever it appears.
        string?[] candidates = ["John Wick - Stern Pinball"];
        var title = GamePageExtractors.SanitizeGameTitle(
            candidates, pageTitle: null, slug: "john-wick");

        Assert.Equal("John Wick", title);
    }

    [Fact]
    public void SanitizeGameTitle_StripsDashSuffixFromPageTitleFallback()
    {
        var title = GamePageExtractors.SanitizeGameTitle(
            candidates: null,
            pageTitle: "Star Wars: Fall of the Empire - Stern Pinball",
            slug: "star-wars-fall-of-the-empire");

        Assert.Equal("Star Wars: Fall of the Empire", title);
    }

    [Fact]
    public void SanitizeGameTitle_StripsPipeSuffixFromCandidate()
    {
        string?[] candidates = ["JAWS | Stern Pinball"];
        var title = GamePageExtractors.SanitizeGameTitle(
            candidates, pageTitle: null, slug: "jaws");

        Assert.Equal("JAWS", title);
    }

    [Fact]
    public void SanitizeGameTitle_FallsBackToPageTitleStrippingSternSuffix()
    {
        string?[] candidates = ["Cookie Settings"];
        var title = GamePageExtractors.SanitizeGameTitle(
            candidates,
            pageTitle: "John Wick | Stern Pinball",
            slug: "john-wick");

        Assert.Equal("John Wick", title);
    }

    [Fact]
    public void SanitizeGameTitle_FallsBackToSlugTitleCaseWhenAllElseFails()
    {
        var title = GamePageExtractors.SanitizeGameTitle(
            candidates: null,
            pageTitle: null,
            slug: "the-walking-dead-remastered");

        Assert.Equal("The Walking Dead Remastered", title);
    }

    [Fact]
    public void SanitizeGameTitle_PicksFirstValidCandidateNotJustNonNull()
    {
        var candidates = new string?[]
        {
            null,
            "  ",
            "Cookie Notice",
            "Godzilla",
            "Some Other Heading",
        };

        var title = GamePageExtractors.SanitizeGameTitle(
            candidates, pageTitle: null, slug: "godzilla");

        Assert.Equal("Godzilla", title);
    }

    [Fact]
    public void SanitizeGameTitle_TrimsWhitespaceFromCandidate()
    {
        string?[] candidates = ["   Foo Fighters   "];
        var title = GamePageExtractors.SanitizeGameTitle(
            candidates, pageTitle: null, slug: "foo-fighters");

        Assert.Equal("Foo Fighters", title);
    }

    // ---------- DeduplicateEditions ----------

    [Fact]
    public void DeduplicateEditions_CollapsesThreefoldDuplicates()
    {
        var input = new[]
        {
            new EditionInfo { Name = "Pro" },
            new EditionInfo { Name = "Pro" },
            new EditionInfo { Name = "Pro" },
            new EditionInfo { Name = "Premium" },
            new EditionInfo { Name = "Premium" },
            new EditionInfo { Name = "Premium" },
        };

        var result = GamePageExtractors.DeduplicateEditions(input);

        Assert.Equal(2, result.Count);
        Assert.Equal("Pro", result[0].Name);
        Assert.Equal("Premium", result[1].Name);
    }

    [Fact]
    public void DeduplicateEditions_MergesNonNullFieldsAcrossDuplicates()
    {
        // Stern's edition cards: outer wrapper has the name, inner wrapper
        // has the price, deepest wrapper has the description. Each one
        // matches a [class*="edition"] selector.
        var input = new[]
        {
            new EditionInfo { Name = "Pro", Msrp = null, Description = null },
            new EditionInfo { Name = "Pro", Msrp = "$6,599", Description = null },
            new EditionInfo { Name = "Pro", Msrp = null, Description = "Standard playfield" },
        };

        var result = GamePageExtractors.DeduplicateEditions(input);

        var single = Assert.Single(result);
        Assert.Equal("Pro", single.Name);
        Assert.Equal("$6,599", single.Msrp);
        Assert.Equal("Standard playfield", single.Description);
    }

    [Fact]
    public void DeduplicateEditions_FirstNonNullWins()
    {
        var input = new[]
        {
            new EditionInfo { Name = "Pro", Msrp = "$6,599" },
            new EditionInfo { Name = "Pro", Msrp = "$9,999" }, // ignored
        };

        var result = GamePageExtractors.DeduplicateEditions(input);

        Assert.Equal("$6,599", Assert.Single(result).Msrp);
    }

    [Fact]
    public void DeduplicateEditions_IsCaseInsensitiveOnName()
    {
        var input = new[]
        {
            new EditionInfo { Name = "Pro" },
            new EditionInfo { Name = "PRO" },
            new EditionInfo { Name = "pro" },
        };

        var result = GamePageExtractors.DeduplicateEditions(input);

        Assert.Single(result);
    }

    [Fact]
    public void DeduplicateEditions_PreservesFirstSeenOrder()
    {
        var input = new[]
        {
            new EditionInfo { Name = "Limited Edition" },
            new EditionInfo { Name = "Pro" },
            new EditionInfo { Name = "Pro" },
            new EditionInfo { Name = "Premium" },
            new EditionInfo { Name = "Limited Edition" },
        };

        var result = GamePageExtractors.DeduplicateEditions(input);

        Assert.Equal(3, result.Count);
        Assert.Equal("Limited Edition", result[0].Name);
        Assert.Equal("Pro", result[1].Name);
        Assert.Equal("Premium", result[2].Name);
    }

    [Fact]
    public void DeduplicateEditions_DropsBlankNames()
    {
        var input = new[]
        {
            new EditionInfo { Name = "" },
            new EditionInfo { Name = "   " },
            new EditionInfo { Name = "Pro" },
        };

        var result = GamePageExtractors.DeduplicateEditions(input);

        Assert.Equal("Pro", Assert.Single(result).Name);
    }

    [Fact]
    public void DeduplicateEditions_TreatsBlankMsrpAsNull()
    {
        var input = new[]
        {
            new EditionInfo { Name = "Pro", Msrp = "   " },
            new EditionInfo { Name = "Pro", Msrp = "$6,599" },
        };

        var result = GamePageExtractors.DeduplicateEditions(input);

        Assert.Equal("$6,599", Assert.Single(result).Msrp);
    }

    [Fact]
    public void DeduplicateEditions_MergesUniqueFeaturesWithoutDuplicates()
    {
        var input = new[]
        {
            new EditionInfo { Name = "Premium", UniqueFeatures = ["Subway", "Magnet"] },
            new EditionInfo { Name = "Premium", UniqueFeatures = ["Magnet", "Diverter"] },
        };

        var result = GamePageExtractors.DeduplicateEditions(input);
        var merged = Assert.Single(result);

        string[] expected = ["Subway", "Magnet", "Diverter"];
        Assert.Equal(expected, merged.UniqueFeatures);
    }

    [Fact]
    public void DeduplicateEditions_ReturnsEmptyForEmptyInput()
    {
        var result = GamePageExtractors.DeduplicateEditions([]);
        Assert.Empty(result);
    }
}
